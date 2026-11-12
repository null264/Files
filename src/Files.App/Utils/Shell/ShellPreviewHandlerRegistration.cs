// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using Microsoft.Win32;

namespace Files.App.Utils.Shell
{
	/// <summary>
	/// Reads how the shell registered a preview handler, so the preview can classify a handler from the
	/// machine it runs on instead of from a compiled-in list of CLSIDs.
	/// </summary>
	public static class ShellPreviewHandlerRegistration
	{
		/// <summary>
		/// Whether the preview handler of <paramref name="extension"/> is the very application that opens
		/// that file type. Office registers Word, Excel and PowerPoint as the preview handlers of their own
		/// document types, and those are the handlers that need the extended window styles cleared while
		/// the pointer is over the preview.
		/// </summary>
		public static bool IsDocumentApplicationHandler(Guid handlerClsid, string? extension)
		{
			var handlerImage = GetServerImagePath(handlerClsid);
			var openerImage = GetOpenCommandImagePath(extension);

			return handlerImage is not null
				&& openerImage is not null
				&& string.Equals(Path.GetFileName(handlerImage), Path.GetFileName(openerImage), StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// The executable or in-process server the handler CLSID is registered with.
		/// </summary>
		public static string? GetServerImagePath(Guid clsid)
		{
			var localServer = GetImageFromCommand(GetClassesValue($@"CLSID\{{{clsid:D}}}\LocalServer32"));
			if (localServer is not null)
				return localServer;

			return GetClassesValue($@"CLSID\{{{clsid:D}}}\InprocServer32");
		}

		/// <summary>
		/// The executable that opens files with <paramref name="extension"/>.
		/// </summary>
		public static string? GetOpenCommandImagePath(string? extension)
		{
			if (string.IsNullOrWhiteSpace(extension))
				return null;

			// The default value of an extension key is its ProgID, whose open verb names the application.
			var progId = GetClassesValue(extension);
			if (string.IsNullOrWhiteSpace(progId))
				return null;

			return GetImageFromCommand(GetClassesValue($@"{progId}\shell\open\command"));
		}

		private static string? GetClassesValue(string subKey)
		{
			// A 32-bit document application registers itself under the WOW6432Node view, so try both.
			foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
			{
				try
				{
					using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
					using var key = classes.OpenSubKey(subKey);

					if (key?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
						return value.Trim();
				}
				catch (Exception)
				{
					// A missing or unreadable key must never break the preview.
				}
			}

			return null;
		}

		private static string? GetImageFromCommand(string? command)
		{
			if (string.IsNullOrWhiteSpace(command))
				return null;

			// Commands look like "C:\...\WINWORD.EXE" /n "%1" or C:\...\VPREVIEW.EXE -preview.
			var text = command.Trim().TrimStart('"');
			var end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);

			return end < 0 ? null : text[..(end + 4)];
		}
	}
}
