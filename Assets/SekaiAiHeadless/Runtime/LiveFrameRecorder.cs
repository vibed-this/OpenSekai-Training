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

			frontCamera.targetTexture = captureRt;
			frontCamera.Render();
			RenderTexture.active = captureRt;
			readbackTex.ReadPixels(new Rect(0, 0, HeadlessCapture.FrameWidth, HeadlessCapture.FrameHeight), 0, 0);
			readbackTex.Apply(false);
			RenderTexture.active = null;
			frontCamera.targetTexture = null;

			string imageName = string.Format(CultureInfo.InvariantCulture, "frame_{0:000000}.jpg", frameId);
			File.WriteAllBytes(Path.Combine(framesDir, imageName), readbackTex.EncodeToJPG(HeadlessCapture.JpgQuality));

			gtWriter.WriteLine(string.Format(
				CultureInfo.InvariantCulture,
				"{{\"frame_id\":{0},\"time\":{1:F4},\"image_path\":\"frames/{2}\",\"music_id\":{3},\"difficulty\":\"{4}\",\"notes\":[{5}]}}",
				frameId,
				now,
				imageName,
				HeadlessCapture.MusicId,
				HeadlessCapture.Difficulty,
				notes));
			frameId++;

			if (frameId % 600 == 0)
			{
				// 心跳：采集阶段平时无日志输出，定时心跳用于区分“正常采集”与“挂起”；Flush 避免中途被杀时 gt.jsonl 为空。
				gtWriter.Flush();
				Debug.LogFormat("HeadlessCapture: progress frames={0} time={1:F2}", frameId, now);
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
