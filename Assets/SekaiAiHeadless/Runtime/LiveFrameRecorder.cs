// Headless 帧采集器：每帧 Render 前景相机，写 JPG + gt.jsonl。
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Sekai.Core.Live;
using Sekai.Live;
using UnityEngine;

namespace SekaiAiHeadless
{
	/// <summary>逐帧 dump（JPG + FrameRecord JSONL），完曲后写 manifest 并退出。</summary>
	public sealed class LiveFrameRecorder : MonoBehaviour
	{
		private SoloLiveController controller;

		private LiveLogic logic;

		private Camera frontCamera;

		private RenderTexture captureRt;

		private Texture2D readbackTex;

		private StreamWriter gtWriter;

		private readonly List<NoteBase> scratchRoots = new List<NoteBase>(256);

		private string framesDir = string.Empty;

		private int frameId;

		private bool finished;

		// 分段计时：单 Stopwatch 取检查点，避免逐帧打日志刷屏，心跳按 600 帧窗口输出均值。
		// 注：用全限定名，不引 System.Diagnostics，避免与 UnityEngine.Debug 二义。
		private readonly System.Diagnostics.Stopwatch stageWatch = new System.Diagnostics.Stopwatch();

		private double totalGtMs;

		private double totalRenderMs;

		private double totalReadbackMs;

		private double totalEncodeMs;

		private double totalWriteMs;

		private double totalWallMs;

		private double windowGtMs;

		private double windowRenderMs;

		private double windowReadbackMs;

		private double windowEncodeMs;

		private double windowWriteMs;

		private double windowWallMs;

		private int windowFrames;

		/// <summary>绑定已就绪的 live 控制器。</summary>
		public void Bind(SoloLiveController liveController)
		{
			controller = liveController;
			logic = liveController.LiveLogicForCapture;
			frontCamera = liveController.BaseCamera;
			if (logic == null || frontCamera == null)
			{
				Debug.LogError("HeadlessCapture: LiveLogic or BaseCamera is null.");
				RequestQuit(1);
				return;
			}

			framesDir = Path.Combine(HeadlessCapture.OutDir, "frames");
			Directory.CreateDirectory(HeadlessCapture.OutDir);
			Directory.CreateDirectory(framesDir);
			captureRt = new RenderTexture(HeadlessCapture.FrameWidth, HeadlessCapture.FrameHeight, 24, RenderTextureFormat.ARGB32);
			readbackTex = new Texture2D(HeadlessCapture.FrameWidth, HeadlessCapture.FrameHeight, TextureFormat.RGB24, false);
			gtWriter = new StreamWriter(Path.Combine(HeadlessCapture.OutDir, "gt.jsonl"), false, new UTF8Encoding(false));
			logic.OnFinished += OnLiveFinished;
			Debug.LogFormat("HeadlessCapture: recorder bound, camera={0}", frontCamera.name);
		}

		private void Update()
		{
			if (finished || controller == null || logic == null || gtWriter == null)
			{
				return;
			}

			if (!controller.JustPlayingState)
			{
				return;
			}

			if (logic.IsNotesAllFinished)
			{
				Finish(0);
				return;
			}

			DumpOneFrame();
		}

		private void DumpOneFrame()
		{
			stageWatch.Reset();
			stageWatch.Start();
			float now = logic.currentFrameInfo.time;
			logic.CollectCaptureNotes(scratchRoots);

			StringBuilder notes = new StringBuilder(1024);
			bool first = true;
			foreach (NoteBase root in scratchRoots)
			{
				if (root == null)
				{
					continue;
				}

				if (root.NoteList != null && root.NoteList.Count > 0)
				{
					foreach (NoteBase child in root.NoteList)
					{
						AppendNoteJson(notes, ref first, child, now);
					}
				}
				else
				{
					AppendNoteJson(notes, ref first, root, now);
				}
			}
			double gtMs = stageWatch.Elapsed.TotalMilliseconds;

			frontCamera.targetTexture = captureRt;
			frontCamera.Render();
			double renderMs = stageWatch.Elapsed.TotalMilliseconds - gtMs;
			RenderTexture.active = captureRt;
			readbackTex.ReadPixels(new Rect(0, 0, HeadlessCapture.FrameWidth, HeadlessCapture.FrameHeight), 0, 0);
			readbackTex.Apply(false);
			RenderTexture.active = null;
			frontCamera.targetTexture = null;
			double readbackMs = stageWatch.Elapsed.TotalMilliseconds - gtMs - renderMs;

			string imageName = string.Format(CultureInfo.InvariantCulture, "frame_{0:000000}.jpg", frameId);
			byte[] jpg = readbackTex.EncodeToJPG(HeadlessCapture.JpgQuality);
			double encodeMs = stageWatch.Elapsed.TotalMilliseconds - gtMs - renderMs - readbackMs;
			File.WriteAllBytes(Path.Combine(framesDir, imageName), jpg);

			gtWriter.WriteLine(string.Format(
				CultureInfo.InvariantCulture,
				"{{\"frame_id\":{0},\"time\":{1:F4},\"image_path\":\"frames/{2}\",\"music_id\":{3},\"difficulty\":\"{4}\",\"notes\":[{5}]}}",
				frameId,
				now,
				imageName,
				HeadlessCapture.MusicId,
				HeadlessCapture.Difficulty,
				notes));
			stageWatch.Stop();
			double wallMs = stageWatch.Elapsed.TotalMilliseconds;
			double writeMs = wallMs - gtMs - renderMs - readbackMs - encodeMs;
			frameId++;

			// 累计 lifetime + 窗口均值：心跳只报均值，避免逐帧日志拖慢主线程。
			totalGtMs += gtMs;
			totalRenderMs += renderMs;
			totalReadbackMs += readbackMs;
			totalEncodeMs += encodeMs;
			totalWriteMs += writeMs;
			totalWallMs += wallMs;
			windowGtMs += gtMs;
			windowRenderMs += renderMs;
			windowReadbackMs += readbackMs;
			windowEncodeMs += encodeMs;
			windowWriteMs += writeMs;
			windowWallMs += wallMs;
			windowFrames++;

			if (frameId % 600 == 0)
			{
				// 心跳：采集阶段平时无日志输出，定时心跳用于区分“正常采集”与“挂起”；Flush 避免中途被杀时 gt.jsonl 为空。
				gtWriter.Flush();
				double divisor = windowFrames > 0 ? windowFrames : 1;
				double windowFps = windowWallMs > 0.0 ? windowFrames * 1000.0 / windowWallMs : 0.0;
				Debug.LogFormat(
					"HeadlessCapture: progress frames={0} time={1:F2} wallFps={2:F1} avgMs(gt={3:F2} render={4:F2} readback={5:F2} encode={6:F2} write={7:F2} wall={8:F2})",
					frameId,
					now,
					windowFps,
					windowGtMs / divisor,
					windowRenderMs / divisor,
					windowReadbackMs / divisor,
					windowEncodeMs / divisor,
					windowWriteMs / divisor,
					windowWallMs / divisor);
				windowGtMs = 0.0;
				windowRenderMs = 0.0;
				windowReadbackMs = 0.0;
				windowEncodeMs = 0.0;
				windowWriteMs = 0.0;
				windowWallMs = 0.0;
				windowFrames = 0;
			}
		}

		private static void AppendNoteJson(StringBuilder sb, ref bool first, NoteBase note, float now)
		{
			if (note == null || !note.HasJudgment || note.IsSkip)
			{
				return;
			}

			if (note.State == NoteState.Done)
			{
				return;
			}

			float progress = note.Progress;
			if (progress < -0.2f || progress > 1.2f)
			{
				return;
			}

			float hitTime = note.MusicScoreInfo.time;
			if (hitTime - now < -1f || hitTime - now > 4f)
			{
				return;
			}

			if (!first)
			{
				sb.Append(',');
			}

			first = false;
			// progress 改用显式 ToString：实测 batchmode 下复合格式 {3:F4} 会原样输出 "F4"，改显式格式化规避。
			sb.AppendFormat(
				CultureInfo.InvariantCulture,
				"{{\"lane\":{0:F3},\"hit_time\":{1:F4},\"category\":\"{2}\",\"progress\":",
				(note.LaneStartF + note.LaneEndF) * 0.5f,
				hitTime,
				note.Category.ToString());
			sb.Append(progress.ToString("F4", CultureInfo.InvariantCulture));
			sb.Append('}');
		}

		private void OnLiveFinished()
		{
			Finish(0);
		}

		private void OnDestroy()
		{
			if (captureRt != null)
			{
				captureRt.Release();
				Destroy(captureRt);
			}

			if (readbackTex != null)
			{
				Destroy(readbackTex);
			}
		}

		private void Finish(int exitCode)
		{
			if (finished)
			{
				return;
			}

			finished = true;
			try
			{
				if (gtWriter != null)
				{
					gtWriter.Flush();
					gtWriter.Dispose();
					gtWriter = null;
				}

				File.WriteAllText(
					Path.Combine(HeadlessCapture.OutDir, "manifest.json"),
					string.Format(
						CultureInfo.InvariantCulture,
						"{{\"version\":\"1\",\"frames\":{0},\"lanes\":12}}",
						frameId),
					new UTF8Encoding(false));
				File.WriteAllText(
					Path.Combine(HeadlessCapture.OutDir, "meta.json"),
					string.Format(
						CultureInfo.InvariantCulture,
						"{{\"music_id\":{0},\"difficulty\":\"{1}\",\"width\":{2},\"height\":{3},\"fps\":{4}}}",
						HeadlessCapture.MusicId,
						HeadlessCapture.Difficulty,
						HeadlessCapture.FrameWidth,
						HeadlessCapture.FrameHeight,
						HeadlessCapture.TargetFps),
					new UTF8Encoding(false));
				Debug.LogFormat("HeadlessCapture: finished frames={0} out={1}", frameId, HeadlessCapture.OutDir);
				if (frameId > 0)
				{
					double divisor = frameId;
					Debug.LogFormat(
						"HeadlessCapture: timing frames={0} avgMs(gt={1:F2} render={2:F2} readback={3:F2} encode={4:F2} write={5:F2} wall={6:F2}) wallFps={7:F1}",
						frameId,
						totalGtMs / divisor,
						totalRenderMs / divisor,
						totalReadbackMs / divisor,
						totalEncodeMs / divisor,
						totalWriteMs / divisor,
						totalWallMs / divisor,
						totalWallMs > 0.0 ? frameId * 1000.0 / totalWallMs : 0.0);
				}
			}
			finally
			{
				RequestQuit(exitCode);
			}
		}

		/// <summary>退出 batchmode（编辑器下带退出码）。</summary>
		public static void RequestQuit(int exitCode)
		{
#if UNITY_EDITOR
			UnityEditor.EditorApplication.Exit(exitCode);
#else
			Application.Quit(exitCode);
#endif
		}
	}
}
