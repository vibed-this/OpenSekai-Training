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

		// 视图锚定框口径：head-only 紧框（全类别一致），长条拖尾由 NoteLineView 另画线，
		// 尾时长走 gt 既有 end_time 字段，不在此框内。纯数学复刻已删除。
		// AppendNoteJson 为实例方法，camera/宽高经由静态缓存传入（逐帧赋值）。
		private static Camera s_BoxCamera;

		private static int s_BoxWidth;

		private static int s_BoxHeight;

		// 框失败原因计数（诊断用，随心跳输出）：无 view / bounds 无效 / 投影丢弃。
		private int totalNoView;

		private int totalNoBounds;

		private int totalClip;

		private int windowNoView;

		private int windowNoBounds;

		private int windowClip;

		// 无 view 按 State 细分（诊断早期零框根因）：Playing 但无 view=spawn 丢弃/被移除。
		private int totalNoViewWait;

		private int totalNoViewPlaying;

		private int totalNoViewLast;

		private int totalNoViewOther;

		private int windowNoViewWait;

		private int windowNoViewPlaying;

		private int windowNoViewLast;

		private int windowNoViewOther;

		// 高进度（progress>0.5，本应有框）失败细分：区分无 view 与投影丢弃。
		private int totalNoViewElig;

		private int totalClipElig;

		private int windowNoViewElig;

		private int windowClipElig;

		// 首个有框帧（诊断早期零框恢复点）。
		private int firstBoxFrameId = -1;

		private float firstBoxTime;

		private int frameBoxed;

		private int noViewSampleLogged;

		// clip 丢弃采样（A 线 box5 诊断）：只记被裁视图的位置/相机，限量防刷屏。
		private int clipSampleLogged;

		private int clipSampleFrameId = -1;

		private int clipSampleInFrame;

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

		// LateUpdate：保证 LiveViewExt.OnUpdate→Move() 之后 dump，GT 时间与视图同帧新鲜。
		private void LateUpdate()
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

			// V2 框投影口径：与 Render 共用 frontCamera + 采集分辨率（视口归一化×宽高）。
			s_BoxCamera = frontCamera;
			s_BoxWidth = HeadlessCapture.FrameWidth;
			s_BoxHeight = HeadlessCapture.FrameHeight;

			StringBuilder notes = new StringBuilder(1024);
			bool first = true;
			frameBoxed = 0;
			clipSampleInFrame = 0;
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
		if (frameBoxed > 0 && firstBoxFrameId < 0)
		{
			firstBoxFrameId = frameId;
			firstBoxTime = now;
		}

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
				int spawnOk = -1;
				int unspawnOk = -1;
				int spawnActive = -1;
				int dropDup = -1;
				int dropNullPool = -1;
				int dropNullView = -1;
				if (controller != null)
				{
					controller.TryGetSpawnStats(out spawnOk, out unspawnOk, out spawnActive, out dropDup, out dropNullPool, out dropNullView);
				}

				Debug.LogFormat(
					"HeadlessCapture: progress frames={0} time={1:F2} wallFps={2:F1} avgMs(gt={3:F2} render={4:F2} readback={5:F2} encode={6:F2} write={7:F2} wall={8:F2}) boxFail(winNoView={9} winNoBounds={10} winClip={11} totalNoView={12} totalNoBounds={13} totalClip={14}) noViewState(winWait={15} winPlaying={16} winLast={17} winOther={18} totWait={19} totPlaying={20} totLast={21} totOther={22}) spawn(ok={23} unspawn={24} active={25} dropDup={26} dropNullPool={27} dropNullView={28} firstBoxFrame={29}) elig(winNoViewElig={30} winClipElig={31} totNoViewElig={32} totClipElig={33})",
					frameId,
					now,
					windowFps,
					windowGtMs / divisor,
					windowRenderMs / divisor,
					windowReadbackMs / divisor,
					windowEncodeMs / divisor,
					windowWriteMs / divisor,
					windowWallMs / divisor,
					windowNoView,
					windowNoBounds,
					windowClip,
					totalNoView,
					totalNoBounds,
					totalClip,
					windowNoViewWait,
					windowNoViewPlaying,
					windowNoViewLast,
					windowNoViewOther,
					totalNoViewWait,
					totalNoViewPlaying,
					totalNoViewLast,
					totalNoViewOther,
					spawnOk,
					unspawnOk,
					spawnActive,
					dropDup,
					dropNullPool,
					dropNullView,
					firstBoxFrameId,
					windowNoViewElig,
					windowClipElig,
					totalNoViewElig,
					totalClipElig);
				windowGtMs = 0.0;
				windowRenderMs = 0.0;
				windowReadbackMs = 0.0;
				windowEncodeMs = 0.0;
				windowWriteMs = 0.0;
				windowWallMs = 0.0;
				windowFrames = 0;
				windowNoView = 0;
				windowNoBounds = 0;
				windowClip = 0;
				windowNoViewWait = 0;
				windowNoViewPlaying = 0;
				windowNoViewLast = 0;
				windowNoViewOther = 0;
				windowNoViewElig = 0;
				windowClipElig = 0;
			}
		}

		/// <summary>视口归一化投影到采集帧像素（左下原点）：避免 batchmode 屏幕回退尺寸与 RT 不一致。</summary>
		private static Vector3 ProjectToFrame(Camera camera, Vector3 world, int frameWidth, int frameHeight)
		{
			Vector3 viewport = camera.WorldToViewportPoint(world);
			return new Vector3(viewport.x * (float)frameWidth, viewport.y * (float)frameHeight, viewport.z);
		}

		/// <summary>Long 系判定：等价于 Category 名含 Long（显式枚举，无字符串分配）。</summary>
		private static bool IsLongCategory(NoteCategory category)
		{
			return category == NoteCategory.Long
				|| category == NoteCategory.FrictionLong
				|| category == NoteCategory.FrictionHideLong;
		}

		/// <summary>首版 class 映射：Normal/Connection=0，Long 系=1，其余=2（写出但训练时忽略，保留可追溯）。</summary>
		private static int ClassifyNote(NoteBase note)
		{
			NoteCategory category = note.Category;
			if (category == NoteCategory.Normal || category == NoteCategory.Connection)
			{
				return 0;
			}

			if (IsLongCategory(category))
			{
				return 1;
			}

			return 2;
		}

		/// <summary>clip 丢弃采样：记被裁视图中心世界/视口坐标与相机位姿。每 300 帧最多记 4 条，总量 200 条封顶（覆盖全曲）。</summary>
		private void MaybeLogClipSample(NoteBase note, float progress, Bounds worldBounds, string verdict, Camera camera, int behindCorners, float cx0, float cx1, float cy0, float cy1)
		{
			if (clipSampleLogged >= 200 || clipSampleInFrame >= 4 || frameId % 300 != 0)
			{
				return;
			}

			if (note == null || camera == null)
			{
				return;
			}

			clipSampleInFrame++;
			clipSampleLogged++;
			clipSampleFrameId = frameId;
			Vector3 center = worldBounds.center;
			Vector3 viewport = camera.WorldToViewportPoint(center);
			Vector3 camPos = camera.transform.position;
			Vector3 camFwd = camera.transform.forward;
			float now = logic != null ? logic.currentFrameInfo.time : -1f;
			Debug.Log(
				"HeadlessCapture: clipSample frame=" + frameId.ToString(CultureInfo.InvariantCulture)
				+ " now=" + now.ToString("F3", CultureInfo.InvariantCulture)
				+ " hit=" + note.MusicScoreInfo.time.ToString("F3", CultureInfo.InvariantCulture)
				+ " id=" + note.Id.ToString(CultureInfo.InvariantCulture)
				+ " cat=" + note.Category.ToString()
				+ " progress=" + progress.ToString("F4", CultureInfo.InvariantCulture)
				+ " verdict=" + verdict
				+ " behindCorners=" + behindCorners.ToString(CultureInfo.InvariantCulture)
				+ " world=(" + center.x.ToString("F3", CultureInfo.InvariantCulture)
				+ "," + center.y.ToString("F3", CultureInfo.InvariantCulture)
				+ "," + center.z.ToString("F3", CultureInfo.InvariantCulture) + ")"
				+ " viewport=(" + viewport.x.ToString("F3", CultureInfo.InvariantCulture)
				+ "," + viewport.y.ToString("F3", CultureInfo.InvariantCulture)
				+ "," + viewport.z.ToString("F3", CultureInfo.InvariantCulture) + ")"
				+ " cornersX=[" + cx0.ToString("F1", CultureInfo.InvariantCulture)
				+ "," + cx1.ToString("F1", CultureInfo.InvariantCulture) + "]"
				+ " cornersY=[" + cy0.ToString("F1", CultureInfo.InvariantCulture)
				+ "," + cy1.ToString("F1", CultureInfo.InvariantCulture) + "]"
				+ " camPos=(" + camPos.x.ToString("F3", CultureInfo.InvariantCulture)
				+ "," + camPos.y.ToString("F3", CultureInfo.InvariantCulture)
				+ "," + camPos.z.ToString("F3", CultureInfo.InvariantCulture) + ")"
				+ " camFwd=(" + camFwd.x.ToString("F3", CultureInfo.InvariantCulture)
				+ "," + camFwd.y.ToString("F3", CultureInfo.InvariantCulture)
				+ "," + camFwd.z.ToString("F3", CultureInfo.InvariantCulture) + ")"
				+ " ortho=" + camera.orthographic.ToString()
				+ " near=" + camera.nearClipPlane.ToString("F3", CultureInfo.InvariantCulture)
				+ " far=" + camera.farClipPlane.ToString("F3", CultureInfo.InvariantCulture));
		}

		/// <summary>
		/// 视图锚定投影：经 controller 取 note 头 view 的世界紧框（head-only，全类别一致），
		/// 8 角点经 ProjectToFrame 投影取 min/max，得像素框（左上原点，采集分辨率口径）。
		/// 长条拖尾由 NoteLineView 另画线，不在此框内；尾时长走 gt 既有 end_time 字段。
		/// 无 view（未 spawn=不可见）或 bounds 无效返回 false；半出屏 clamp，全出屏丢弃。
		/// 每 note 每帧 8 次 WorldToViewportPoint。
		/// </summary>
		/// <returns>可框返回 true；丢弃返回 false。</returns>
		private bool ProjectNoteBox(NoteBase note, float progress, Camera camera, int frameWidth, int frameHeight, out float x, out float y, out float w, out float h)
		{
			x = 0f;
			y = 0f;
			w = 0f;
			h = 0f;
			if (note == null || camera == null || controller == null || frameWidth <= 0 || frameHeight <= 0)
			{
				return false;
			}

			if (!controller.TryGetNoteView(note, out global::Sekai.BaseNoteView view) || view == null)
			{
				// 未 spawn=不可见，不出框。
				totalNoView++;
				windowNoView++;
				if (progress > 0.5f)
				{
					totalNoViewElig++;
					windowNoViewElig++;
				}
				NoteState noViewState = note.State;
				if (noViewState == NoteState.Playing)
				{
					totalNoViewPlaying++;
					windowNoViewPlaying++;
				}
				else if (noViewState == NoteState.Wait)
				{
					totalNoViewWait++;
					windowNoViewWait++;
				}
				else if (noViewState == NoteState.Last)
				{
					totalNoViewLast++;
					windowNoViewLast++;
				}
				else
				{
					totalNoViewOther++;
					windowNoViewOther++;
				}

				if ((noViewState == NoteState.Playing || progress > 0.5f) && noViewSampleLogged < 6)
				{
					noViewSampleLogged++;
					bool everSpawned = controller != null && controller.WasNoteSpawned(note);
					Debug.LogFormat(
						"HeadlessCapture: noView sample frame={0} now={1} hit={2} cat={3} progress={4} state={5} id={6} everSpawned={7}",
						frameId,
						logic != null ? logic.currentFrameInfo.time : -1f,
						note.MusicScoreInfo.time,
						note.Category,
						note.Progress,
						noViewState,
						note.Id,
						everSpawned);
				}

				return false;
			}

			if (!view.TryGetWorldBounds(out Bounds worldBounds))
			{
				totalNoBounds++;
				windowNoBounds++;
				return false;
			}

			Vector3 boundsMin = worldBounds.min;
			Vector3 boundsMax = worldBounds.max;
			float xMin = float.MaxValue;
			float xMax = float.MinValue;
			float yMin = float.MaxValue;
			float yMax = float.MinValue;
			// 相机与键平面近共面（视口 z≈0.001 量级）时，包围盒厚度可使个别角点 z 微负；
			// 渲染侧该键完全可见，故只跳过负角点、8 角全负才按屏外丢弃（此前任一角负即整框丢弃，误杀高进度大框）。
			int behindCorners = 0;
			for (int i = 0; i < 8; i++)
			{
				Vector3 corner = new Vector3(
					(i & 1) == 0 ? boundsMin.x : boundsMax.x,
					(i & 2) == 0 ? boundsMin.y : boundsMax.y,
					(i & 4) == 0 ? boundsMin.z : boundsMax.z);
				Vector3 projected = ProjectToFrame(camera, corner, frameWidth, frameHeight);
				if (projected.z < 0f)
				{
					behindCorners++;
					continue;
				}

				if (projected.x < xMin)
				{
					xMin = projected.x;
				}

				if (projected.x > xMax)
				{
					xMax = projected.x;
				}

				if (projected.y < yMin)
				{
					yMin = projected.y;
				}

				if (projected.y > yMax)
				{
					yMax = projected.y;
				}
			}

			if (behindCorners == 8)
			{
				totalClip++;
				windowClip++;
				if (progress > 0.5f)
				{
					totalClipElig++;
					windowClipElig++;
				}

				MaybeLogClipSample(note, progress, worldBounds, "behind:all", camera, behindCorners, 0f, 0f, 0f, 0f);
				return false;
			}

			// Unity 屏坐标（左下原点）转图像坐标（左上原点），半出屏 clamp，全出屏丢弃。
			float ix0 = xMin;
			float ix1 = xMax;
			float iy0 = (float)frameHeight - yMax;
			float iy1 = (float)frameHeight - yMin;
			if (ix1 < 0f || ix0 > (float)frameWidth || iy1 < 0f || iy0 > (float)frameHeight)
			{
				totalClip++;
				windowClip++;
				if (progress > 0.5f)
				{
					totalClipElig++;
					windowClipElig++;
				}

				MaybeLogClipSample(note, progress, worldBounds, "outside:xMin" + xMin.ToString("F1", CultureInfo.InvariantCulture) + ":xMax" + xMax.ToString("F1", CultureInfo.InvariantCulture) + ":yMin" + yMin.ToString("F1", CultureInfo.InvariantCulture) + ":yMax" + yMax.ToString("F1", CultureInfo.InvariantCulture), camera, behindCorners, xMin, xMax, yMin, yMax);
				return false;
			}

			if (ix0 < 0f)
			{
				ix0 = 0f;
			}

			if (ix1 > (float)frameWidth)
			{
				ix1 = (float)frameWidth;
			}

			if (iy0 < 0f)
			{
				iy0 = 0f;
			}

			if (iy1 > (float)frameHeight)
			{
				iy1 = (float)frameHeight;
			}

			float bw = ix1 - ix0;
			float bh = iy1 - iy0;
			if (bw <= 0f || bh <= 0f)
			{
				totalClip++;
				windowClip++;
				if (progress > 0.5f)
				{
					totalClipElig++;
					windowClipElig++;
				}

				MaybeLogClipSample(note, progress, worldBounds, "degenerate", camera, behindCorners, xMin, xMax, yMin, yMax);
				return false;
			}

			x = ix0;
			y = iy0;
			w = bw;
			h = bh;
			return true;
		}

		private void AppendNoteJson(StringBuilder sb, ref bool first, NoteBase note, float now)
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

		// 视图锚定框：有 view 才写 box/class_id；无 view（未 spawn=不可见）条目保留但不写框，
		// 训练侧按有无 box 过滤。
		float boxX;
		float boxY;
		float boxW;
		float boxH;
		bool hasBox = ProjectNoteBox(note, progress, s_BoxCamera, s_BoxWidth, s_BoxHeight, out boxX, out boxY, out boxW, out boxH);

			int classId = ClassifyNote(note);

			if (!first)
			{
				sb.Append(',');
			}

		first = false;
		// batchmode 下复合格式中的数值格式符（如 {3:F4}）会原样输出，故全部浮点先显式 ToString，
		// 模板里只留无格式符占位；direction/category 取自枚举 ToString，不含引号可直插。
		float laneStart = note.LaneStartF;
		float laneEnd = note.LaneEndF;
		float endTime = hitTime;
		if (note is LongNote longNote && longNote.ChildNote != null)
		{
			endTime = longNote.ChildNote.MusicScoreInfo.time;
		}

		sb.AppendFormat(
			CultureInfo.InvariantCulture,
			"{{\"lane\":{0},\"hit_time\":{1},\"lane_start\":{2},\"lane_end\":{3},\"end_time\":{4},\"category\":\"{5}\",\"direction\":\"{6}\",\"speed_ratio\":{7},\"progress\":",
			((laneStart + laneEnd) * 0.5f).ToString("F3", CultureInfo.InvariantCulture),
			hitTime.ToString("F4", CultureInfo.InvariantCulture),
			laneStart.ToString("F3", CultureInfo.InvariantCulture),
			laneEnd.ToString("F3", CultureInfo.InvariantCulture),
			endTime.ToString("F4", CultureInfo.InvariantCulture),
			note.Category.ToString(),
			note.Direction.ToString(),
			note.speedRatio.ToString("F3", CultureInfo.InvariantCulture));
		sb.Append(progress.ToString("F4", CultureInfo.InvariantCulture));
		if (!hasBox)
		{
			// 无框条目：训练侧按有无 box 过滤，此处直接封口。
			sb.Append("}");
			return;
		}

		frameBoxed++;

		// 有框：浮点先 ToString（沿用 batchmode 坑对策），整数直插。
		sb.AppendFormat(
			CultureInfo.InvariantCulture,
			",\"box\":{{\"x\":{0},\"y\":{1},\"w\":{2},\"h\":{3}}},\"class_id\":{4}}}",
			boxX.ToString("F1", CultureInfo.InvariantCulture),
			boxY.ToString("F1", CultureInfo.InvariantCulture),
			boxW.ToString("F1", CultureInfo.InvariantCulture),
			boxH.ToString("F1", CultureInfo.InvariantCulture),
			classId);
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
					"{{\"music_id\":{0},\"difficulty\":\"{1}\",\"width\":{2},\"height\":{3},\"fps\":{4},\"note_speed\":{5}}}",
					HeadlessCapture.MusicId,
					HeadlessCapture.Difficulty,
					HeadlessCapture.FrameWidth,
					HeadlessCapture.FrameHeight,
					HeadlessCapture.TargetFps,
					HeadlessCapture.NoteSpeed.ToString("F1", CultureInfo.InvariantCulture)),
				new UTF8Encoding(false));
				Debug.LogFormat("HeadlessCapture: finished frames={0} out={1}", frameId, HeadlessCapture.OutDir);
				Debug.LogFormat("HeadlessCapture: firstBox frame={0} time={1} noViewState(wait={2} playing={3} last={4} other={5})",
					firstBoxFrameId,
					firstBoxTime,
					totalNoViewWait,
					totalNoViewPlaying,
					totalNoViewLast,
					totalNoViewOther);
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
