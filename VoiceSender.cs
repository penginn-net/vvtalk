using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Discord;
using Discord.Audio;

namespace VVTalk;

/// <summary>
/// サーバー(ギルド)ごとの読み上げ担当。VCへの接続・キュー・音声合成・再生をまとめて扱う。
/// </summary>
public sealed class VoiceSender
{
	// VOICEVOXに48kHz / ステレオで合成させる (WAVのdataチャンクをそのままPCMとして送る)
	private const int OutputSamplingRate = 48000;

	private readonly IVoiceChannel _voiceChannel;
	private readonly string _voicevox;
	private readonly HttpClient _http;
	private readonly Action<VoiceSender> _onExited;
	private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
	private readonly CancellationTokenSource _cts = new();

	public ulong VoiceChannelId => _voiceChannel.Id;
	public ulong TextChannelId { get; set; }
	public int Speaker { get; set; }
	public double SpeedScale { get; set; } = 1;
	public double PitchScale { get; set; } = 0;

	/// <param name="voiceChannel">接続するVC</param>
	/// <param name="voicevox">VOICEVOXのベースURL (末尾の / を含む)</param>
	/// <param name="http">VOICEVOXとの通信に使うHttpClient</param>
	/// <param name="onExited">読み上げが終了した時に呼ばれる (ギルドの登録解除用)</param>
	public VoiceSender(IVoiceChannel voiceChannel, string voicevox, HttpClient http, Action<VoiceSender> onExited)
	{
		_voiceChannel = voiceChannel;
		_voicevox = voicevox;
		_http = http;
		_onExited = onExited;
	}

	/// <summary>
	/// 読み上げ処理をバックグラウンドで開始する。接続もそこで行う (ゲートウェイのイベントハンドラを塞がないため)。
	/// </summary>
	public void Start()
	{
		_ = Task.Run(RunAsync);
	}

	/// <summary>
	/// 読み上げ対象の文字列をキューに追加する。
	/// </summary>
	public void Enqueue(string text)
	{
		if (string.IsNullOrEmpty(text) || _cts.IsCancellationRequested) return;
		_queue.Writer.TryWrite(text);
	}

	/// <summary>
	/// 読み上げを停止する。キューを破棄し、ループ終了時にVCから切断する。
	/// </summary>
	public void Stop()
	{
		_cts.Cancel();
		while (_queue.Reader.TryRead(out _)) { }
	}

	private async Task RunAsync()
	{
		var ct = _cts.Token;
		IAudioClient? audio = null;
		AudioOutStream? pcm = null;
		try
		{
			audio = await _voiceChannel.ConnectAsync(selfDeaf: true);
			audio.Disconnected += OnDisconnectedAsync;
			// 接続ごとに1回だけ作り、発話間で使い回す (バッファリングと送信間隔の制御はDiscord.Net側が行う)
			pcm = audio.CreatePCMStream(AudioApplication.Voice);

			await foreach (var text in _queue.Reader.ReadAllAsync(ct))
			{
				Console.WriteLine("読み上げ対象 " + text);
				try
				{
					await SpeakAsync(pcm, text, ct);
				}
				catch (Exception e) when (!ct.IsCancellationRequested)
				{
					// 1件の失敗で読み上げループを止めない
					Console.WriteLine(e);
				}
			}
		}
		catch (OperationCanceledException)
		{
			// 停止要求による終了
		}
		catch (Exception e)
		{
			Console.WriteLine(e);
		}
		finally
		{
			Console.WriteLine("ループ脱出による切断");
			Stop();
			if (audio is not null) audio.Disconnected -= OnDisconnectedAsync;
			pcm?.Dispose();
			try
			{
				await _voiceChannel.DisconnectAsync();
			}
			catch (Exception e)
			{
				Console.WriteLine(e);
			}
			// 切断が済んでから登録を外す (次の start_talk の接続を巻き込んで切らないため)
			_onExited(this);
		}
	}

	private Task OnDisconnectedAsync(Exception ex)
	{
		// 音声接続が切れたら読み上げを終了する (後始末と登録解除はループ側で行う)
		Stop();
		return Task.CompletedTask;
	}

	private async Task SpeakAsync(AudioOutStream pcm, string text, CancellationToken ct)
	{
		// 音声合成クエリを作成し、速度・音高・出力形式を指定する
		using var queryRes = await _http.PostAsync(
			$"{_voicevox}audio_query?speaker={Speaker}&text={Uri.EscapeDataString(text)}", null, ct);
		queryRes.EnsureSuccessStatusCode();
		var json = JsonNode.Parse(await queryRes.Content.ReadAsStreamAsync(ct)) as JsonObject
			?? throw new InvalidDataException("audio_query の応答が不正です");
		json["speedScale"] = SpeedScale;
		json["pitchScale"] = PitchScale;
		json["outputSamplingRate"] = OutputSamplingRate;
		json["outputStereo"] = true;

		// 音声合成 (WAVが返る)
		using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json.ToJsonString()));
		content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		using var synthRes = await _http.PostAsync($"{_voicevox}synthesis?speaker={Speaker}", content, ct);
		synthRes.EnsureSuccessStatusCode();
		var wav = await synthRes.Content.ReadAsByteArrayAsync(ct);

		var (offset, length) = FindPcmChunk(wav);
		await pcm.WriteAsync(wav.AsMemory(offset, length), ct);
		await pcm.FlushAsync(ct);
	}

	/// <summary>
	/// WAVのチャンクを順にたどり、fmtを確認したうえで data チャンクの位置と長さを返す。
	/// ヘッダ長は固定せず、チャンク構造から求める。
	/// </summary>
	private static (int Offset, int Length) FindPcmChunk(byte[] wav)
	{
		if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
		{
			throw new InvalidDataException("WAVではありません");
		}

		bool formatChecked = false;
		int pos = 12;
		while (pos + 8 <= wav.Length)
		{
			var id = wav.AsSpan(pos, 4);
			long size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4, 4));
			int body = pos + 8;
			if (id.SequenceEqual("fmt "u8) && body + 16 <= wav.Length)
			{
				// 48kHz / ステレオ / 16bit PCM であることを確認する
				int channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2, 2));
				uint rate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 4, 4));
				int bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14, 2));
				if (channels != 2 || rate != OutputSamplingRate || bits != 16)
				{
					throw new InvalidDataException($"想定外のWAV形式です ({channels}ch {rate}Hz {bits}bit)");
				}
				formatChecked = true;
			}
			else if (id.SequenceEqual("data"u8))
			{
				if (!formatChecked) throw new InvalidDataException("fmtチャンクがありません");
				int length = (int)Math.Min(size, wav.Length - body);
				return (body, length);
			}
			// チャンクは偶数バイト境界に揃えられる
			long next = body + size + (size & 1);
			pos = (int)Math.Min(next, wav.Length);
		}
		throw new InvalidDataException("dataチャンクが見つかりません");
	}
}
