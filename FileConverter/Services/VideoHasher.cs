using System.Security.Cryptography;
using System.Text;

namespace FileConverter.Services
{
    public class VideoHasher
    {
        private const int ChunkSize = 4096;

        public static string GetHash(byte[] videoData)
        {
            if (videoData == null)
            {
                throw new ArgumentNullException(nameof(videoData));
            }

            long fileSize = videoData.Length;

            try
            {
                using (var md5 = MD5.Create())
                {
                    byte[] sizeBytes = BitConverter.GetBytes(fileSize);
                    md5.TransformBlock(sizeBytes, 0, sizeBytes.Length, null, 0);

                    if (fileSize == 0)
                    {
                        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    }
                    else
                    {
                        byte[] buffer = new byte[ChunkSize];
                        int bytesToProcess;
                        bool isFinalBlock = false;

                        // Обрабатываем начало
                        bytesToProcess = (int)Math.Min(ChunkSize, fileSize);
                        Array.Copy(videoData, 0, buffer, 0, bytesToProcess);
                        isFinalBlock = (bytesToProcess == fileSize);
                        if (isFinalBlock)
                        {
                            md5.TransformFinalBlock(buffer, 0, bytesToProcess);
                        }
                        else
                        {
                            md5.TransformBlock(buffer, 0, bytesToProcess, null, 0);
                        }

                        // Обрабатываем середину
                        if (!isFinalBlock && fileSize > ChunkSize * 2)
                        {
                            long middleStartIndex = (fileSize / 2) - (ChunkSize / 2);
                            bytesToProcess = ChunkSize;
                            Array.Copy(videoData, middleStartIndex, buffer, 0, bytesToProcess);
                            isFinalBlock = (fileSize <= middleStartIndex + bytesToProcess);

                            if (isFinalBlock)
                            {
                                md5.TransformFinalBlock(buffer, 0, bytesToProcess);
                            }
                            else
                            {
                                md5.TransformBlock(buffer, 0, bytesToProcess, null, 0);
                            }
                        }

                        // Обрабатываем конец
                        if (!isFinalBlock && fileSize > ChunkSize)
                        {
                            bytesToProcess = (int)Math.Min(ChunkSize, fileSize);
                            long endStartIndex = fileSize - bytesToProcess;

                            if (endStartIndex < ChunkSize && fileSize > ChunkSize)
                            {
                                bytesToProcess = (int)(fileSize - ChunkSize);
                                endStartIndex = ChunkSize;
                            }

                            if (bytesToProcess > 0)
                            {
                                Array.Copy(videoData, endStartIndex, buffer, 0, bytesToProcess);
                                md5.TransformFinalBlock(buffer, 0, bytesToProcess);
                            }
                            else
                            {
                                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            }
                        }
                    }

                    byte[] hashBytes = md5.Hash;
                    StringBuilder sb = new StringBuilder(hashBytes.Length * 2);
                    foreach (byte b in hashBytes)
                    {
                        sb.Append(b.ToString("x2"));
                    }
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error hashing video data: {ex.Message}", ex);
            }
        }
    }
} 