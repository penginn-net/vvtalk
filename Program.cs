using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;

namespace VVTalk;

public static class Program
{
	public static async Task<int> Main()
	{
		// 設定は環境変数から読む (未設定なら起動時に止める)
		string? token = Environment.GetEnvironmentVariable("TOKEN");
		if (string.IsNullOrWhiteSpace(token))
		{
			Console.Error.WriteLine("環境変数 TOKEN が設定されていません");
			return 1;
		}
		string? voicevox = Environment.GetEnvironmentVariable("VOICEVOX");
		if (string.IsNullOrWhiteSpace(voicevox))
		{
			Console.Error.WriteLine("環境変数 VOICEVOX が設定されていません (例: http://voicevox:50021/)");
			return 1;
		}

		var client = new DiscordSocketClient(new DiscordSocketConfig
		{
			GatewayIntents = GatewayIntents.Guilds
				| GatewayIntents.GuildVoiceStates
				| GatewayIntents.GuildMessages
				| GatewayIntents.MessageContent,
		});
		var bot = new Bot(client, voicevox);

		client.Log += message =>
		{
			Console.WriteLine(message.ToString());
			return Task.CompletedTask;
		};
		client.Ready += bot.OnReadyAsync;
		client.SlashCommandExecuted += bot.OnSlashCommandAsync;
		client.MessageReceived += bot.OnMessageReceivedAsync;

		await client.LoginAsync(TokenType.Bot, token);
		await client.StartAsync();

		// 終了はプロセスの終了 (Ctrl+C / docker stop) に任せる
		await Task.Delay(Timeout.Infinite);
		return 0;
	}
}

/// <summary>
/// Discordのイベント処理。サーバーごとの読み上げ担当 (VoiceSender) を管理する。
/// </summary>
internal sealed class Bot
{
	private readonly DiscordSocketClient _client;
	private readonly string _voicevox;
	private readonly HttpClient _http = new();
	private readonly ConcurrentDictionary<ulong, VoiceSender> _senders = new();
	// start_talk / end_talk の登録状態を確認して書き換える間の排他 (同一サーバーで二重に接続しないため)
	private readonly object _gate = new();

	public Bot(DiscordSocketClient client, string voicevox)
	{
		_client = client;
		_voicevox = voicevox;
	}

	/// <summary>
	/// 起動完了時にスラッシュコマンドを登録する。HTTP処理なのでタスクに逃がしてゲートウェイを塞がない。
	/// </summary>
	public Task OnReadyAsync()
	{
		_ = Task.Run(async () =>
		{
			try
			{
				// DiscordSocketClientでは明示的実装のためIDiscordClient経由で呼ぶ
				await ((IDiscordClient)_client).BulkOverwriteGlobalApplicationCommand(
				[
					new SlashCommandBuilder()
						.WithName("start_talk")
						.WithDescription("読み上げを開始します")
						.WithDefaultMemberPermissions(GuildPermission.Speak)
						.WithContextTypes(InteractionContextType.Guild)
						.AddOption("speaker", ApplicationCommandOptionType.Integer, "声の質を整数値で指定")
						.AddOption("speed", ApplicationCommandOptionType.Number, "読み上げ速度倍率")
						.AddOption("pitch", ApplicationCommandOptionType.Number, "説明文")
						.Build(),
					new SlashCommandBuilder()
						.WithName("end_talk")
						.WithDescription("読み上げを停止します")
						.WithDefaultMemberPermissions(GuildPermission.Speak)
						.WithContextTypes(InteractionContextType.Guild)
						.Build(),
				]);
			}
			catch (Exception e)
			{
				Console.WriteLine(e);
			}
		});
		Console.WriteLine("bot起動");
		return Task.CompletedTask;
	}

	public Task OnSlashCommandAsync(SocketSlashCommand cmd)
	{
		// ゲートウェイのイベントハンドラを塞がないよう、処理は別タスクで行う
		_ = Task.Run(() => HandleSlashCommandAsync(cmd));
		return Task.CompletedTask;
	}

	private async Task HandleSlashCommandAsync(SocketSlashCommand cmd)
	{
		try
		{
			// サーバー外 (DMなど) は無視する
			if (cmd.GuildId is not ulong gid || cmd.User is not SocketGuildUser member) return;

			switch (cmd.CommandName)
			{
				case "start_talk":
					await StartTalkAsync(cmd, gid, member);
					break;
				case "end_talk":
					await EndTalkAsync(cmd, gid, member);
					break;
				default:
					Console.WriteLine($"Unknown command {cmd.CommandName} used by {cmd.User}");
					break;
			}
		}
		catch (Exception e)
		{
			Console.WriteLine(e);
		}
	}

	private async Task StartTalkAsync(SocketSlashCommand cmd, ulong gid, SocketGuildUser member)
	{
		if (!member.GuildPermissions.Speak)
		{
			await cmd.RespondAsync("権限不足", ephemeral: true);
			return;
		}
		var voiceChannel = member.VoiceChannel;
		if (voiceChannel is null)
		{
			await cmd.RespondAsync("VCに入ってから実行する必要があります", ephemeral: true);
			return;
		}
		if (cmd.ChannelId is not ulong textChannelId) return;

		string? error = null;
		lock (_gate)
		{
			_senders.TryGetValue(gid, out var sender);
			if (sender is not null && sender.VoiceChannelId != voiceChannel.Id)
			{
				error = "他のVCに移動するには切断する必要があります";
			}
			else
			{
				bool isNew = sender is null;
				sender ??= new VoiceSender(voiceChannel, _voicevox, _http, s => _senders.TryRemove(new KeyValuePair<ulong, VoiceSender>(gid, s)));
				sender.TextChannelId = textChannelId;
				if (GetOption(cmd, "speaker") is { } speaker) sender.Speaker = Convert.ToInt32(speaker);
				if (GetOption(cmd, "speed") is { } speedScale) sender.SpeedScale = Convert.ToDouble(speedScale);
				if (GetOption(cmd, "pitch") is { } pitchScale) sender.PitchScale = Convert.ToDouble(pitchScale);
				if (isNew)
				{
					// 登録してから開始する (接続が即座に失敗しても登録解除できるように)
					_senders[gid] = sender;
					sender.Start();
				}
				Console.WriteLine($"speaker{sender.Speaker}");
				Console.WriteLine($"speedScale{sender.SpeedScale}");
				Console.WriteLine($"pitchScale{sender.PitchScale}");
			}
		}

		if (error is not null)
		{
			await cmd.RespondAsync(error, ephemeral: true);
		}
		else
		{
			await cmd.RespondAsync("読み上げ起動");
		}
	}

	private async Task EndTalkAsync(SocketSlashCommand cmd, ulong gid, SocketGuildUser member)
	{
		if (!member.GuildPermissions.Speak)
		{
			await cmd.RespondAsync("権限不足", ephemeral: true);
			return;
		}
		if (_senders.TryRemove(gid, out var sender))
		{
			sender.Stop();
			await cmd.RespondAsync("読み上げ切断");
		}
		else
		{
			await cmd.RespondAsync("起動してない", ephemeral: true);
		}
	}

	public Task OnMessageReceivedAsync(SocketMessage message)
	{
		// DMなどサーバー外のメッセージは無視する
		if (message.Channel is not IGuildChannel guildChannel) return Task.CompletedTask;
		if (_senders.TryGetValue(guildChannel.GuildId, out var sender) && sender.TextChannelId == message.Channel.Id)
		{
			sender.Enqueue(ToTalkString(message));
		}
		return Task.CompletedTask;
	}

	private static object? GetOption(SocketSlashCommand cmd, string name)
	{
		return cmd.Data.Options.FirstOrDefault(o => o.Name == name)?.Value;
	}

	/// <summary>
	/// 読み上げ用の文字列に変換する (コードブロック・URL・添付ファイル・長文の扱い)
	/// </summary>
	private static string ToTalkString(IMessage m)
	{
		string s = m.CleanContent;
		if (s.StartsWith("```", StringComparison.Ordinal))
		{
			s = "コードブロック省略";
		}
		s = Normalize.UrlCut(s);
		foreach (var f in m.Attachments)
		{
			s += Normalize.FileName(f.Filename);
		}
		if (s.Length > 100)
		{
			s = "長文省略";
		}
		return s;
	}
}
