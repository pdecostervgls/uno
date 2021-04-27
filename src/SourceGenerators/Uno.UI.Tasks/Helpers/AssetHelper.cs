using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Uno.UI.Tasks.Helpers
{
	class AssetHelper
	{
		public static bool IsImageAsset(string path)
		{
			var extension = Path.GetExtension(path).ToLowerInvariant();
			return extension == ".png"
				|| extension == ".jpg"
				|| extension == ".jpeg"
				|| extension == ".gif";
		}

		public static bool IsFontAsset(string path)
		{
			var extension = Path.GetExtension(path).ToLowerInvariant();
			return extension == ".ttf"
				|| extension == ".eot"
				|| extension == ".woff"
				|| extension == ".woff2";
		}
	}
}
