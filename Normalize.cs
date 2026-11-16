using System.Text;
using System.Text.RegularExpressions;

namespace VVTalk;

/// <summary>
/// 読み上げ用の文字列加工 (URL省略・ファイル名の種類判定)
/// </summary>
public static class Normalize
{
	// URL判定基準
	private static readonly Regex UrlPattern = new(@"https?://\S+", RegexOptions.CultureInvariant);

	/// <summary>
	/// URLを省略する。1つ目を「URL省略」(複数なら件数付き)に置き換え、2つ目以降は削除する。
	/// </summary>
	public static string UrlCut(string text)
	{
		int count = UrlPattern.Matches(text).Count;
		if (count == 0) return text;

		bool first = true;
		return UrlPattern.Replace(text, _ =>
		{
			if (!first) return "";
			first = false;
			return count == 1 ? "URL省略" : $"{count}件のURLを省略";
		});
	}

	/// <summary>
	/// ファイル名から種類を表す読み上げ文字列を返す (大文字小文字は区別する)
	/// </summary>
	public static string FileName(string g)
	{
		string r = "ファイル";
		if (EndsWithAny(g, ".png", ".gif", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff",
				".xcf", ".clip", ".psd", ".svg"))
		{
			r = "画像";
		}
		else if (EndsWithAny(g, ".txt"))
		{
			r = "テキストファイル";
		}
		else if (EndsWithAny(g, ".rs", ".js", ".java", ".ts", ".py", ".cs", ".c"))
		{
			r = "ソースファイル";
		}
		else if (EndsWithAny(g, ".class"))
		{
			r = "クラスファイル";
		}
		else if (EndsWithAny(g, ".h"))
		{
			r = "ヘッダーファイル";
		}
		else if (EndsWithAny(g, ".ini", ".xml", ".json"))
		{
			r = "データファイル";
		}
		else if (EndsWithAny(g, ".css", ".scss"))
		{
			r = "スタイルシート";
		}
		else if (EndsWithAny(g, ".html"))
		{
			r = "エイチティーエムエル";
		}
		else if (EndsWithAny(g, ".av1", ".mp4", ".avi", ".mov", ".flv"))
		{
			r = "動画";
		}
		else if (EndsWithAny(g, ".opus", ".ogg", ".wav", ".mp3", ".mid", ".midi"))
		{
			r = "音楽";
		}
		else if (EndsWithAny(g, ".zip", ".gz", ".7z", ".xz", ".lzh"))
		{
			r = "圧縮ファイル";
		}
		else if (EndsWithAny(g, ".tar"))
		{
			r = "アーカイブファイル";
		}
		else if (EndsWithAny(g, ".log"))
		{
			r = "ログファイル";
		}
		else
		{
			// 未定義の拡張子は1文字ずつ区切って読み上げる (例: ".abc" -> "a,b,c,ファイル")
			int li = g.LastIndexOf('.');
			if (li >= 0 && li + 1 < g.Length)
			{
				Console.WriteLine("未定義ファイル" + g);
				var sb = new StringBuilder();
				foreach (char c in g.AsSpan(li + 1))
				{
					sb.Append(c).Append(',');
				}
				r = sb.Append("ファイル").ToString();
			}
		}
		return r;
	}

	private static bool EndsWithAny(string g, params string[] exts)
	{
		foreach (var ext in exts)
		{
			if (g.EndsWith(ext, StringComparison.Ordinal)) return true;
		}
		return false;
	}
}
