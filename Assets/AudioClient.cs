using System;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Concurrent;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class AudioClient : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TMP_InputField ipInputField;
    [SerializeField] private TMP_InputField portInputField;
    [SerializeField] private Button connectButton;
    [SerializeField] private Button stopButton;
    [SerializeField] private TextMeshProUGUI log;
    
    [Header("Audio Settings")]
    [SerializeField] private int sampleRate = 44100;
    [SerializeField] private int channels = 1;
    [SerializeField] private int recordingLength = 10;
    [SerializeField] private int sendIntervalMs = 1000;
    [SerializeField] private int bufferSize = 4096;
    [SerializeField] private AudioSource audioSource;
    
    [Header("Compression Settings")]
    // [SerializeField] private bool enableCompression = true;
    // [SerializeField] private bool enableValidation = true;

    private TcpClient client;
    private NetworkStream stream;
    private volatile bool isRunning;
    private Thread sendThread;
    private Thread receiveThread;
    private AudioClip recordingClip;
    private AudioClip playbackClip;
    private readonly object lockObject = new();
    private ConcurrentQueue<float[]> audioDataQueue = new();    // ConcurrentQueue是线程安全的
    private const int MAX_QUEUE_SIZE = 100;  // 限制队列大小以防内存溢出
    
    private int lastRecordPosition;
    private float[] fadeBuffer;
    private const float FADE_DURATION = 0.1f; // 100ms淡入淡出
    private bool isDisposed;
    
    private SynchronizationContext mainThread;

    private void Start()
    {
        mainThread = SynchronizationContext.Current;
        InitializeUI();
        InitializeAudio();
    }

    // 初始化音频组件
    private void InitializeAudio()
    {
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0; // 2D音频
        audioSource.loop = true;
    }

    // 初始化UI
    private void InitializeUI()
    {
        connectButton.onClick.AddListener(ConnectAndStart);
        stopButton.onClick.AddListener(StopAll);
        stopButton.interactable = false;
    }

    // 停止所有操作
    private void StopAll()
    {
        try
        {
            if (!isRunning) return; // 防止重复调用

            // 1. 停止所有操作
            isRunning = false;
            
            // 2. 停止录音
            if (Microphone.IsRecording(null))
            {
                Microphone.End(null);
            }
            
            // 3. 停止音频播放
            if (audioSource != null && audioSource.isPlaying)
            {
                audioSource.Stop();
            }
            
            // 4. 等待线程安全终止
            WaitForThreadsToEnd();
            
            // 5. 关闭网络连接
            CloseConnection();
            
            // 6. 更新UI状态
            stopButton.interactable = false;
            connectButton.interactable = true;
            
            // 7. 清理音频资源
            CleanupAudioResources();
            
            UpdateLog("Session stopped successfully.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error during stop operation: {ex}");
            UpdateLog("Error occurred while stopping session.");
        }
    }
    
    private void WaitForThreadsToEnd()
    {
        try
        {
            // 给予线程一个合理的时间来完成当前操作
            if (sendThread != null && sendThread.IsAlive)
            {
                if (!sendThread.Join(TimeSpan.FromSeconds(2)))
                {
                    Debug.LogWarning("Send thread did not terminate gracefully");
                }
            }

            if (receiveThread != null && receiveThread.IsAlive)
            {
                if (!receiveThread.Join(TimeSpan.FromSeconds(2)))
                {
                    Debug.LogWarning("Receive thread did not terminate gracefully");
                }
            }
        }
        catch (ThreadStateException ex)
        {
            Debug.LogError($"Error waiting for threads to end: {ex}");
        }
    }
    
    private void CloseConnection()
    {
        lock (lockObject)
        {
            try
            {
                NetworkStream currentStream = stream;
                TcpClient currentClient = client;
                
                stream = null;
                client = null;

                try
                {
                    currentStream?.Flush();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Error flushing stream: {ex.Message}");
                }
                
                try
                {
                    currentStream?.Close();
                    currentStream?.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Error closing stream: {ex.Message}");
                }

                try
                {
                    if (currentClient?.Connected == true)
                    {
                        currentClient.Close();
                    }
                    currentClient?.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Error closing client: {ex.Message}");
                }

                while (audioDataQueue.TryDequeue(out _)) { }
            }
            finally
            {
                stream = null;
                client = null;
            }
        }
    }
    
    private void CleanupAudioResources()
    {
        // 清理AudioClip资源
        if (recordingClip != null)
        {
            Destroy(recordingClip);
            recordingClip = null;
        }

        if (playbackClip != null)
        {
            Destroy(playbackClip);
            playbackClip = null;
        }
    }

    /// <summary>
    /// 连接到服务器并开始会话
    /// </summary>
    private void ConnectAndStart()
    {
        string ip = ipInputField.text;
        if (string.IsNullOrWhiteSpace(ip))
        {
            UpdateLog("Please enter an IP address.");
            return;
        }

        if (!int.TryParse(portInputField.text, out int port) || port <= 0 || port > 65535)
        {
            UpdateLog("Invalid port number. Please enter a number between 1 and 65535.");
            return;
        }

        if (ConnectToServer(ip, port))
        {
            StartSession();
            connectButton.interactable = false;
            stopButton.interactable = true;
        }
    }

    /// <summary>
    /// 连接到服务器
    /// </summary>
    private bool ConnectToServer(string ip, int port)
    {
        try
        {
            client = new TcpClient();
            var result = client.BeginConnect(ip, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            
            if (!success)
            {
                throw new Exception("Connection attempt timed out.");
            }
            
            // 连接成功，获取网络流
            client.EndConnect(result);
            stream = client.GetStream();
            UpdateLog("Connected to server.");
            return true;
        }
        catch (Exception e)
        {
            UpdateLog($"Error connecting to server: {e.Message}");
            CloseConnection();
            return false;
        }
    }

    /// <summary>
    /// 开始会话
    /// </summary>
    private void StartSession()
    {
        // 检查麦克风是否可用
        if (!Microphone.devices.Length.Equals(0))
        {
            isRunning = true;
            audioDataQueue = new ConcurrentQueue<float[]>();
            
            // 启动录音
            recordingClip = Microphone.Start(null, true, recordingLength, sampleRate);
            
            // 创建播放用的AudioClip
            playbackClip = AudioClip.Create("PlaybackClip", bufferSize, channels, sampleRate, true, OnAudioRead);
            audioSource.clip = playbackClip;
            audioSource.Play();

            // 启动发送和接收线程
            sendThread = new Thread(SendAudioData) { IsBackground = true };
            receiveThread = new Thread(ReceiveAudioData) { IsBackground = true };
            
            sendThread.Start();
            receiveThread.Start();
            
            UpdateLog("Session started... Press Stop to end.");
        }
        else
        {
            UpdateLog("No microphone detected!");
            StopAll();
        }
    }

    // 发送音频数据
    private void SendAudioData()
    {
        int samplesPerFrame = sampleRate * sendIntervalMs / 1000;
        float[] samples = new float[samplesPerFrame * channels];
        
        while (isRunning && client?.Connected == true)
        {
            try
            {
                lock (lockObject)
                {
                    var currentPosition = Microphone.GetPosition(null);
                    if (currentPosition < 0 || recordingClip == null) continue;

                    int readPosition = lastRecordPosition;
                    int samplesToRead = (currentPosition - lastRecordPosition + recordingClip.samples) 
                                        % recordingClip.samples;

                    // 循环处理所有可用数据
                    while (samplesToRead >= samples.Length && isRunning)
                    {
                        recordingClip.GetData(samples, readPosition);
                        byte[] bytes = ConvertFloatToByte(samples);
                        
                        if (stream != null)
                        {
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush(); // 确保数据立即发送
                        }
                        
                        readPosition = (readPosition + samples.Length) % recordingClip.samples;
                        samplesToRead -= samples.Length;
                        lastRecordPosition = readPosition;
                    }
                }
                Thread.Sleep(sendIntervalMs);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error sending audio data: {e.Message}");
                mainThread.Post(_ => StopAll(), null);
                break;
            }
        }
    }

    /// <summary>
    /// 接收音频数据
    /// </summary>
    private void ReceiveAudioData()
    {
        byte[] buffer = new byte[bufferSize * sizeof(float)];
    
        while (isRunning && client?.Connected == true)
        {
            try
            {
                if (stream?.CanRead == true && stream.DataAvailable)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        float[] audioData = ConvertByteToFloat(buffer, bytesRead);
                    
                        // ConcurrentQueue是线程安全的，不需要额外的锁
                        while (audioDataQueue.Count >= MAX_QUEUE_SIZE)
                        {
                            audioDataQueue.TryDequeue(out _);
                        }
                        audioDataQueue.Enqueue(audioData);
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error receiving audio data: {e.Message}");
                mainThread.Post(_ => StopAll(), null);
                break;
            }
        }
    }

    /// <summary>
    /// 当音频源需要读取数据时调用
    /// </summary>
    private readonly object fadeBufferLock = new object();
    private void OnAudioRead(float[] data)
    {
        lock (fadeBufferLock)
        {
            if (fadeBuffer == null || fadeBuffer.Length != data.Length)
            {
                fadeBuffer = new float[data.Length];
            }
        }

        float[] currentFadeBuffer;
        lock (fadeBufferLock)
        {
            currentFadeBuffer = fadeBuffer;
        }

        if (audioDataQueue.TryDequeue(out float[] audioData))
        {
            int copyLength = Mathf.Min(data.Length, audioData.Length);
            Array.Copy(audioData, currentFadeBuffer, copyLength);
            
            int fadeSamples = (int)(FADE_DURATION * sampleRate);
            for (int i = 0; i < copyLength; i++)
            {
                float fade = i < fadeSamples ? i / (float)fadeSamples : 1f;
                data[i] = currentFadeBuffer[i] * fade;
            }
        }
        else
        {
            // 优化后的淡出处理
            int fadeSamples = (int)(FADE_DURATION * sampleRate);
            for (int i = 0; i < data.Length; i++)
            {
                if (i < fadeSamples)
                {
                    float fade = 1 - (i / (float)fadeSamples);
                    data[i] = currentFadeBuffer[i] * fade;
                }
                else
                {
                    data[i] = 0f;
                }
            }
        }
    }

    private static byte[] ConvertFloatToByte(float[] data)
    {
        byte[] bytes = new byte[data.Length * sizeof(float)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] ConvertByteToFloat(byte[] bytes, int bytesRead)
    {
        int floatCount = bytesRead / sizeof(float);
        float[] floats = new float[floatCount];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytesRead);
        return floats;
    }

    private void UpdateLog(string message)
    {
        if (SynchronizationContext.Current == mainThread)
        {
            // 如果在主线程，直接更新
            log.text = message;
        }
        else
        {
            // 如果在其他线程，使用Post方法
            mainThread.Post(_ => log.text = message, null);
        }
    }

    private void OnApplicationQuit()
    {
        StopAll();
        CloseConnection();
    }

    private void OnDisable()
    {
        StopAll();
    }

    private void OnDestroy()
    {
        StopAll();
    }
}