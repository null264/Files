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
			if (data.Length < 64)
			{
				return $"{data.Length}_{Convert.ToBase64String(data)}";
			}

			int len = data.Length;

			long head = BitConverter.ToInt64(data.Slice(0, 8));
			long tail = BitConverter.ToInt64(data.Slice(len - 8, 8));

			// Stride Sampling
			int step = len / 16;
			long sampleHash = 0;
			for (int i = 0; i < 16; i++)
			{
				// DJB2/Fowler-Noll-Vo with zero allocation
				sampleHash = (sampleHash * 31) + data[i * step];
			}

			return $"{len}_{head:X}_{tail:X}_{sampleHash:X}";
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