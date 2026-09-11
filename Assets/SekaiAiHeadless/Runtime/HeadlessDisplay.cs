// Headless 布局覆盖：batchmode 下 Unity Screen 回退 640x480（2026-09-11 实测），游戏按 4:3 布局，
// 采集帧两侧各留 160px 黑边。采集时强制布局按采集分辨率走；非采集时原样透传 Screen。
using UnityEngine;

namespace SekaiAiHeadless
{
	/// <summary>采集期显示尺寸覆盖。BeforeSceneLoad 激活，早于一切布局 Awake。</summary>
	public static class HeadlessDisplay
	{
		public static bool OverrideActive { get; private set; }

		public static int Width { get; private set; } = 1280;

		public static int Height { get; private set; } = 720;

		/// <summary>激活覆盖，幂等。非 headless 进程永不调用，保持透传。</summary>
		public static void Activate(int width, int height)
		{
			if (width > 0)
			{
				Width = width;
			}

			if (height > 0)
			{
				Height = height;
			}

			OverrideActive = true;
		}

		/// <summary>布局用宽：覆盖中返回采集宽，否则返回真实 Screen。</summary>
		public static int W()
		{
			return OverrideActive ? Width : Screen.width;
		}

		/// <summary>布局用高：覆盖中返回采集高，否则返回真实 Screen。</summary>
		public static int H()
		{
			return OverrideActive ? Height : Screen.height;
		}

		/// <summary>布局用宽高比。非法时回退到调用方原 fallback，保持非覆盖路径语义不变。</summary>
		public static float Aspect(float fallback)
		{
			int h = H();
			return h > 0 ? (float)W() / h : fallback;
		}
	}
}
