using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

/// <summary>
/// 音频数据包，包含压缩数据和校验信息
/// </summary>
public class AudioPacket
{
    public byte[] CompressedData { get; set; }
    public uint Checksum { get; set; }
    public int OriginalLength { get; set; }
    public bool IsValid { get; set; }
    
    private const int HEADER_SIZE = 9; // 4(checksum) + 4(length) + 1(isValid)
    
    public byte[] ToBytes()
    {
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(ms))
        {
            writer.Write(Checksum);
            writer.Write(OriginalLength);
            writer.Write(IsValid);
            writer.Write(CompressedData);
            return ms.ToArray();
        }
    }
    
    public static AudioPacket FromBytes(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        return new AudioPacket
        {
            Checksum = reader.ReadUInt32(),
            OriginalLength = reader.ReadInt32(),
            IsValid = reader.ReadBoolean(),
            CompressedData = reader.ReadBytes(data.Length - HEADER_SIZE)
        };
    }
}

/// <summary>
/// 音频数据处理工具类
/// </summary>
public static class AudioProcessor
{
    // CRC32表
    private static readonly uint[] Crc32Table = GenerateCrc32Table();
    
    /// <summary>
    /// 压缩音频数据并添加校验
    /// </summary>
    public static AudioPacket CompressAndValidate(float[] audioData)
    {
        byte[] originalBytes = new byte[audioData.Length * sizeof(float)];
        Buffer.BlockCopy(audioData, 0, originalBytes, 0, originalBytes.Length);
        
        // 压缩数据
        byte[] compressedData;
        using (MemoryStream outputStream = new MemoryStream())
        using (DeflateStream deflateStream = new DeflateStream(outputStream, CompressionLevel.Optimal))
        {
            deflateStream.Write(originalBytes, 0, originalBytes.Length);
            deflateStream.Close();
            compressedData = outputStream.ToArray();
        }
        
        // 计算校验和
        uint checksum = CalculateCrc32(originalBytes);
        
        return new AudioPacket
        {
            CompressedData = compressedData,
            Checksum = checksum,
            OriginalLength = originalBytes.Length,
            IsValid = true
        };
    }
    
    /// <summary>
    /// 解压音频数据并验证
    /// </summary>
    public static float[] DecompressAndVerify(AudioPacket packet)
    {
        try
        {
            // 解压数据
            byte[] decompressedBytes = new byte[packet.OriginalLength];
            using (MemoryStream inputStream = new MemoryStream(packet.CompressedData))
            using (DeflateStream deflateStream = new DeflateStream(inputStream, CompressionMode.Decompress))
            {
                deflateStream.Read(decompressedBytes, 0, decompressedBytes.Length);
            }
            
            // 验证校验和
            uint checksum = CalculateCrc32(decompressedBytes);
            if (checksum != packet.Checksum)
            {
                Debug.LogWarning("Audio data validation failed!");
                return null;
            }
            
            // 转换回float数组
            float[] audioData = new float[packet.OriginalLength / sizeof(float)];
            Buffer.BlockCopy(decompressedBytes, 0, audioData, 0, decompressedBytes.Length);
            return audioData;
        }
        catch (Exception e)
        {
            Debug.LogError($"Error decompressing audio data: {e.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// 生成CRC32查找表
    /// </summary>
    private static uint[] GenerateCrc32Table()
    {
        uint[] table = new uint[256];
        uint polynomial = 0xEDB88320;
        
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int j = 0; j < 8; j++)
            {
                if ((value & 1) == 1)
                {
                    value = (value >> 1) ^ polynomial;
                }
                else
                {
                    value >>= 1;
                }
            }
            table[i] = value;
        }
        
        return table;
    }
    
    /// <summary>
    /// 计算CRC32校验和
    /// </summary>
    private static uint CalculateCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        
        foreach (byte b in data)
        {
            byte index = (byte)((crc & 0xFF) ^ b);
            crc = (crc >> 8) ^ Crc32Table[index];
        }
        
        return ~crc;
    }
}