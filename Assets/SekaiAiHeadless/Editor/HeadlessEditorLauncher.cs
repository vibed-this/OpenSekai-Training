// Headless Editor 入口：executeMethod 自举（确定性设置 + 开 Core 场景 + 进 PlayMode）。
using UnityEditor;
using UnityEditor.SceneManagement;

namespace SekaiAiHeadless.Editor
{
	/// <summary>batchmode 启动器：Unity -batchmode -executeMethod 接入点。</summary>
	public static class HeadlessEditorLauncher
	{
		public static void Launch()
		{
			var settings = new Sekai.LiveSettingData
			{
				// 与 Boot() 同源：命令行缺省即 DefaultNoteSpeed，显式非法直接抛错退出。
				NoteSpeed = HeadlessCapture.ReadNoteSpeedArg(),
				IsMirror = false,
			};
			Sekai.LiveSettingData.SaveToStorage(settings);
			Sekai.SUS.Converter.InvalidateLiveSettingCache();

			EditorSceneManager.OpenScene("Assets/Sekai/Scenes/Core.unity");
			EditorApplication.EnterPlaymode();
		}
	}
}
