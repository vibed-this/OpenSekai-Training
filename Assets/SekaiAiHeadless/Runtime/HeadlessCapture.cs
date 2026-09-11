// Headless 自动采数据：虚拟时钟 + 自举。由 batchmode 命令行驱动，无需 GUI。
using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace SekaiAiHeadless
{
	/// <summary>Headless 采集总控：命令行解析、虚拟时钟、自举 BootData。</summary>
	public static class HeadlessCapture
	{
		public static bool Enabled { get; private set; }

		public static string SusPath { get; private set; } = string.Empty;

		public static string OutDir { get; private set; } = string.Empty;

		public static int MusicId { get; private set; }

		public static string Difficulty { get; private set; } = "master";

		public static int FrameWidth { get; private set; } = 1280;

		public static int FrameHeight { get; private set; } = 720;

		public static int TargetFps { get; private set; } = 60;

		public static int JpgQuality { get; private set; } = 85;

		/// <summary>采集流速（NoteSpeed）：progress→像素映射的唯一自由参数，必须随 meta 存档。</summary>
		public const float DefaultNoteSpeed = 6f;

		public static float NoteSpeed { get; private set; } = DefaultNoteSpeed;

		private static bool booted;

		private static long virtualMusicMs;

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void AutoBoot()
		{
			TryBoot();
		}

		/// <summary>解析命令行并自举，幂等。</summary>
		public static bool TryBoot()
		{
			if (booted)
			{
				return Enabled;
			}

			booted = true;
			if (!TryParseArgs())
			{
				return false;
			}

			Enabled = true;
			virtualMusicMs = 0L;
			// 布局覆盖必须在场景加载前激活：batchmode 下 Screen 回退 640x480（实测），
			// 不覆盖则游戏按 4:3 布局，采集帧两侧黑边。
			HeadlessDisplay.Activate(FrameWidth, FrameHeight);
			Debug.LogFormat(
				"HeadlessCapture: display override {0}x{1} (Screen={2}x{3})",
				FrameWidth,
				FrameHeight,
				Screen.width,
				Screen.height);
			Boot();
			return true;
		}

		private static string GetArg(string key)
		{
			string[] args = Environment.GetCommandLineArgs();
			for (int i = 0; i + 1 < args.Length; i++)
			{
				if (string.Equals(args[i], key, StringComparison.Ordinal))
				{
					return args[i + 1];
				}
			}

			return null;
		}

		private static bool TryParseArgs()
		{
			string sus = GetArg("--sekai-sus");
			string outDir = GetArg("--sekai-out");
			if (string.IsNullOrEmpty(sus) || string.IsNullOrEmpty(outDir))
			{
				return false;
			}

			if (!File.Exists(sus))
			{
				Debug.LogErrorFormat("HeadlessCapture: sus not found: {0}", sus);
				return false;
			}

			SusPath = sus;
			OutDir = outDir;
			string musicId = GetArg("--sekai-music-id");
			if (!string.IsNullOrEmpty(musicId))
			{
				int.TryParse(musicId, out int parsed);
				MusicId = parsed;
			}

			string difficulty = GetArg("--sekai-difficulty");
			if (!string.IsNullOrEmpty(difficulty))
			{
				Difficulty = difficulty;
			}

			string width = GetArg("--sekai-width");
			if (!string.IsNullOrEmpty(width) && int.TryParse(width, out int w) && w > 0)
			{
				FrameWidth = w;
			}

			string height = GetArg("--sekai-height");
			if (!string.IsNullOrEmpty(height) && int.TryParse(height, out int h) && h > 0)
			{
				FrameHeight = h;
			}

			string fps = GetArg("--sekai-fps");
			if (!string.IsNullOrEmpty(fps) && int.TryParse(fps, out int f) && f > 0)
			{
				TargetFps = f;
			}

		string jpg = GetArg("--sekai-jpg");
		if (!string.IsNullOrEmpty(jpg) && int.TryParse(jpg, out int q))
		{
			JpgQuality = Math.Max(1, Math.Min(100, q));
		}

		try
		{
			NoteSpeed = ReadNoteSpeedArg();
		}
		catch (ArgumentOutOfRangeException ex)
		{
			Debug.LogErrorFormat("HeadlessCapture: {0}", ex.Message);
			return false;
		}

		return true;
	}

	/// <summary>解析 --sekai-speed：缺省为 DefaultNoteSpeed；显式非法抛异常（fast-fail，无兜底）。</summary>
	public static float ReadNoteSpeedArg()
	{
		string raw = GetArg("--sekai-speed");
		if (string.IsNullOrEmpty(raw))
		{
			return DefaultNoteSpeed;
		}

		if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || v <= 0f)
		{
			throw new ArgumentOutOfRangeException(
				"sekai-speed",
				string.Format(CultureInfo.InvariantCulture, "非法的 --sekai-speed：{0}（须为正数）。", raw));
		}

		return v;
	}

		/// <summary>虚拟音乐时钟：按固定步进推进，与真实帧率解耦。</summary>
		public static long AdvanceVirtualMusicMs(long currentMs)
		{
			virtualMusicMs = Math.Max(virtualMusicMs, currentMs) + (long)Math.Round(1000.0 / Math.Max(1, TargetFps));
			return virtualMusicMs;
		}

		private static void Boot()
		{
			string susText = File.ReadAllText(SusPath);
			var score = new Sekai.SUS.Converter().Convert(susText, true, false);

			var bootData = new Sekai.FreeLiveBootData(
				0,
				Difficulty,
				0,
				0,
				Sekai.LivePlayMode.Free,
				Sekai.LiveMusicData.CollaborationModeState.Off,
				Sekai.MusicCategory.original);
			bootData.IsAuto = true;
			bootData.IsCustomMusicScore = true;
			bootData.IsOfficialMusicScore = false;
			bootData.ReturnScreenType = null;
			bootData.canSkipDisplayMusicInfo = true;
			bootData.ReleaseTransitionBeforeMusicStart = true;
		bootData.LiveSettingData = new Sekai.LiveSettingData
		{
			NoteSpeed = NoteSpeed,
			IsMirror = false,
		};
			bootData.MusicData.MusicScore = score;
			bootData.MusicData.IsTestPlay = false;
			bootData.MusicData.IsUseCustomScore = true;
			bootData.MusicData.StartMusicTimeMs = 0L;
			bootData.MusicData.PlayStartEffectEnabled = false;
			Sekai.UserDataManager.Instance.FreeLiveBootData = bootData;
			Sekai.Core.EntryPoint.PlayMode = Sekai.Core.PlayMode.SoloLive;

			var runnerObject = new GameObject("SekaiAiHeadlessRunner");
			UnityEngine.Object.DontDestroyOnLoad(runnerObject);
			runnerObject.AddComponent<HeadlessBootstrapRunner>();
			Debug.LogFormat("HeadlessCapture: booted sus={0} out={1}", SusPath, OutDir);
		}
	}
}
