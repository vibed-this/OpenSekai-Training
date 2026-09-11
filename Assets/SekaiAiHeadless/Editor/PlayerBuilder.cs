// Standalone 采集包构建：单 Core 场景 + Mono + release，无 Editor 依赖。
// 用法（batchmode）：-executeMethod SekaiAiHeadless.Editor.PlayerBuilder.BuildCapturePlayer --sekai-build-out <exe 绝对路径>
using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SekaiAiHeadless.Editor
{
	/// <summary>打 headless 采集包：场景只含 Core（player 开机直达 SoloLive 预置位）。</summary>
	public static class PlayerBuilder
	{
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

		public static void BuildCapturePlayer()
		{
			string outPath = GetArg("--sekai-build-out");
			if (string.IsNullOrEmpty(outPath))
			{
				Debug.LogError("PlayerBuilder: 缺 --sekai-build-out <exe 绝对路径>。");
				EditorApplication.Exit(1);
				return;
			}

			bool prevRunInBackground = PlayerSettings.runInBackground;
			ScriptingImplementation prevBackend =
				PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
		// 并行采集时后台实例不被节流：构建期强制开，构建完恢复，避免脏 ProjectSettings。
		PlayerSettings.runInBackground = true;
		PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
			try
			{
				// 脚本化构建要求 StreamingAssets/data 预置 AssetBundleInfo（gitignore 生成物）：缺失则先打。
				if (!Sekai.EditorTools.OpenSekaiAssetBundleBuildPipeline.HasPackagedAssetBundleInfo())
				{
					Debug.Log("PlayerBuilder: 打 AssetBundles（StandaloneWindows64）。");
					Sekai.EditorTools.OpenSekaiAssetBundleBuildPipeline.BuildForTarget(
						BuildTarget.StandaloneWindows64,
						true);
				}

				var options = new BuildPlayerOptions
				{
					scenes = new[] { "Assets/Sekai/Scenes/Core.unity" },
					locationPathName = outPath,
					target = BuildTarget.StandaloneWindows64,
					options = BuildOptions.None,
				};
				BuildReport report = BuildPipeline.BuildPlayer(options);
				Debug.LogFormat(
					"PlayerBuilder: result={0} out={1}",
					report.summary.result,
					outPath);
				if (report.summary.result != BuildResult.Succeeded)
				{
					EditorApplication.Exit(1);
				}
			}
			finally
			{
				PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, prevBackend);
				PlayerSettings.runInBackground = prevRunInBackground;
			}
		}
	}
}
