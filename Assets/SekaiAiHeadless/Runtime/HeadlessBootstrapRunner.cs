// Headless 自举轮询：等 SoloLiveController 就绪后挂载采集器。
using Sekai.Core.Live;
using UnityEngine;

namespace SekaiAiHeadless
{
	/// <summary>等待 live 就绪并挂载帧采集器，超时则报错退出。</summary>
	public sealed class HeadlessBootstrapRunner : MonoBehaviour
	{
		private float waitStart;

		private bool recorderAttached;

		private void Awake()
		{
			waitStart = Time.realtimeSinceStartup;
		}

		private void Update()
		{
			if (recorderAttached)
			{
				return;
			}

			SoloLiveController controller = FindObjectOfType<SoloLiveController>();
			if (controller != null && controller.LiveLogicForCapture != null && controller.BaseCamera != null)
			{
				GameObject recorderObject = new GameObject("SekaiAiFrameRecorder");
				Object.DontDestroyOnLoad(recorderObject);
				LiveFrameRecorder recorder = recorderObject.AddComponent<LiveFrameRecorder>();
				recorder.Bind(controller);
				recorderAttached = true;
				Destroy(gameObject);
				return;
			}

			if (Time.realtimeSinceStartup - waitStart > 180f)
			{
				Debug.LogError("HeadlessCapture: timed out waiting for SoloLiveController.");
				LiveFrameRecorder.RequestQuit(1);
				Destroy(gameObject);
			}
		}
	}
}
