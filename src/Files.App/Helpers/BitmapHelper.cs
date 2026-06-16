// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using Windows.Foundation.Metadata;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Microsoft.Extensions.Caching.Memory;

namespace Files.App.Helpers
{
	internal static class BitmapHelper
	{
		private static readonly MemoryCache ImageCache = new MemoryCache(new MemoryCacheOptions
		{
			SizeLimit = 400
		});

		public static async Task<BitmapImage?> ToBitmapAsync(this byte[]? data, int decodeSize = -1)
		{
			if (data is null)
				return null;

			string surrogateKey = GenerateFastFingerprint(data);
			string fullCacheKey = $"{surrogateKey}_{decodeSize}";

			Task<BitmapImage?> bitmapTask = ImageCache.GetOrCreate(fullCacheKey, entry =>
			{
				entry.Size = 1;
				entry.SlidingExpiration = TimeSpan.FromMinutes(5); 
				entry.AbsoluteExpiration = DateTimeOffset.Now.Add(TimeSpan.FromMinutes(15));

				return CreateBitmapCoreAsync(data, decodeSize);
			})!;

			var result = await bitmapTask;

			if (result is null)
			{
				ImageCache.Remove(fullCacheKey);
			}

			return result;
		}

		public static async Task<BitmapImage?> CreateBitmapCoreAsync(byte[]? data, int decodeSize = -1)
		{
			if (data is null)
			{
				return null;
			}

			try
			{
				using var ms = new MemoryStream(data);
				var image = new BitmapImage();
				if (decodeSize > 0)
				{
					image.DecodePixelWidth = decodeSize;
					image.DecodePixelHeight = decodeSize;
				}
				image.DecodePixelType = DecodePixelType.Logical;
				await image.SetSourceAsync(ms.AsRandomAccessStream());
				return image;
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static string GenerateFastFingerprint(ReadOnlySpan<byte> data)
		{
			// 针对极小的字节数组，直接转换为 Base64 作为 Key
			if (data.Length < 32)
			{
				return $"{data.Length}_{Convert.ToBase64String(data)}";
			}

			// 针对正常图片，读取【文件头 16 字节】和【文件尾 16 字节】作为特征
			// 图片的文件头包含 Magic Number（如 PNG, JFIF 标记），文件尾包含数据结束标记或元数据
			// 结合文件总长度，碰撞概率几乎为 0，且完全不需要对几百 KB 的数组做全量 Hash 计算
			long head1 = BitConverter.ToInt64(data.Slice(0, 8));
			long head2 = BitConverter.ToInt64(data.Slice(8, 8));
			long tail1 = BitConverter.ToInt64(data.Slice(data.Length - 16, 8));
			long tail2 = BitConverter.ToInt64(data.Slice(data.Length - 8, 8));

			// 使用十六进制格式化字符串，保证 Key 的紧凑性
			return $"{data.Length}_{head1:X}_{head2:X}_{tail1:X}_{tail2:X}";
		}

		/// <summary>
		/// Rotates the image at the specified file path.
		/// </summary>
		/// <param name="filePath">The file path to the image.</param>
		/// <param name="rotation">The rotation direction.</param>
		/// <remarks>
		/// https://learn.microsoft.com/uwp/api/windows.graphics.imaging.bitmapdecoder?view=winrt-22000
		/// https://learn.microsoft.com/uwp/api/windows.graphics.imaging.bitmapencoder?view=winrt-22000
		/// </remarks>
		public static async Task RotateAsync(string filePath, BitmapRotation rotation)
		{
			try
			{
				if (string.IsNullOrEmpty(filePath))
				{
					return;
				}

				var file = await StorageHelpers.ToStorageItem<IStorageFile>(filePath);
				if (file is null)
				{
					return;
				}

				var fileStreamRes = await FilesystemTasks.Wrap(() => file.OpenAsync(FileAccessMode.ReadWrite).AsTask());
				using IRandomAccessStream fileStream = fileStreamRes.Result;
				if (fileStream is null)
				{
					return;
				}

				BitmapDecoder decoder = await BitmapDecoder.CreateAsync(fileStream);
				using var memStream = new InMemoryRandomAccessStream();
				BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(memStream, decoder);

				for (int i = 0; i < decoder.FrameCount - 1; i++)
				{
					encoder.BitmapTransform.Rotation = rotation;
					await encoder.GoToNextFrameAsync();
				}

				encoder.BitmapTransform.Rotation = rotation;

				await encoder.FlushAsync();

				memStream.Seek(0);
				fileStream.Seek(0);
				fileStream.Size = 0;

				await RandomAccessStream.CopyAsync(memStream, fileStream);
			}
			catch (Exception ex)
			{
				var errorDialog = new ContentDialog()
				{
					Title = Strings.FailedToRotateImage.GetLocalizedResource(),
					Content = ex.Message,
					PrimaryButtonText = Strings.OK.GetLocalizedResource(),
				};

				if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
					errorDialog.XamlRoot = MainWindow.Instance.Content.XamlRoot;

				await errorDialog.TryShowAsync();
			}
		}

		/// <summary>
		/// This function encodes a software bitmap with the specified encoder and saves it to a file
		/// </summary>
		/// <param name="softwareBitmap"></param>
		/// <param name="outputFile"></param>
		/// <param name="encoderId">The guid of the image encoder type</param>
		/// <returns></returns>
		public static async Task SaveSoftwareBitmapToFileAsync(SoftwareBitmap softwareBitmap, BaseStorageFile outputFile, Guid encoderId)
		{
			using IRandomAccessStream stream = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
			// Create an encoder with the desired format
			BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);

			// Set the software bitmap
			encoder.SetSoftwareBitmap(softwareBitmap);

			try
			{
				await encoder.FlushAsync();
			}
			catch (Exception err)
			{
				const int WINCODEC_ERR_UNSUPPORTEDOPERATION = unchecked((int)0x88982F81);
				switch (err.HResult)
				{
					case WINCODEC_ERR_UNSUPPORTEDOPERATION:
						// If the encoder does not support writing a thumbnail, then try again
						// but disable thumbnail generation.
						encoder.IsThumbnailGenerated = false;
						break;

					default:
						throw;
				}
			}

			if (encoder.IsThumbnailGenerated == false)
			{
				await encoder.FlushAsync();
			}
		}
	}
}