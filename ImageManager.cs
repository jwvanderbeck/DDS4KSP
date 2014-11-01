using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.DirectX;
using Microsoft.DirectX.Direct3D;

namespace DDS4KSPcs
{
	static class ImageManager
	{
		//enums for conversion parameter
		public enum NormalMapConversion
		{
			Automatic = 0,
			ForceNormal = 1,
			ForceNotNormal = 2
		}
		public enum MipmapSetting
		{
			Generate = 0,
			DontGenerate = 1
		}
		public enum ResizeSetting
		{
			AllowResize = 0,
			DontAllowResize = 1
		}
		public enum OutputFormat
		{
			AutoCompressed = 0,
			AutoUncompressed = 1,
			ForceDXT1 = 2,
			ForceDXT5 = 3,
			ForceRgb8 = 4,
			ForceArgb8 = 5
		}
		public enum FileType
		{
			MBM = 0,
			Standard = 1
		}

		//this module will handle everything related to DX: loading and saving files, and also swizzling if necessary
		private static Device dev;
		private static PresentParameters pParameters;
		public static int MinHeightForCompressed = 64;
		public static int MinWidthForCompressed = 64;

		private static readonly List<Format> bppFormats32 = new List<Format>();
		private static readonly List<Format> bbpFormats24 = new List<Format>();
		private static readonly List<Format> bppFormatsSpecial = new List<Format>();

		public enum DDSOutputFormat
		{
			DDSRgb = 0,
			DDSRgba = 1,
			DDSNormal = 2
		}

		//called once at startup
		public static void Init()
		{
			//list supported formats
			bppFormats32.Clear();
			bbpFormats24.Clear();
			bppFormatsSpecial.Clear();
			bppFormats32.Add(Format.A8R8G8B8);
			bppFormats32.Add(Format.R8G8B8G8);
			bbpFormats24.Add(Format.R8G8B8);
			bbpFormats24.Add(Format.X8R8G8B8);
			//those are usual format in KSPs mods, but the program can't convert them properly. I'll have to find a way to convert those
			bppFormatsSpecial.Add(Format.P8);
			bppFormatsSpecial.Add(Format.L8);
			bppFormatsSpecial.Add(Format.A8L8);
			bppFormatsSpecial.Add(Format.L16);
			//initialize PresentationParameters for the graphic device
			pParameters = new PresentParameters {Windowed = true, SwapEffect = SwapEffect.Discard};
			//Create the graphic device
			ResetDevice();
		}

		//reset graphic device, to save memory
		private static void ResetDevice()
		{
			if(dev != null)
				dev.Reset(pParameters);
			else
				dev = new Device(0, DeviceType.Hardware, MainForm.Instance.Handle, CreateFlags.HardwareVertexProcessing, pParameters);
		}

		//refresh labels infos
		public static void RefreshInfo(string sFilePath, out string txtRes, out string txtColDepth, out string txtNormal)
		{
			var infos1 = TextureLoader.ImageInformationFromFile(sFilePath);
			txtRes = "Resolution : " + infos1.Width + "x" + infos1.Height;
			txtColDepth = "Color depth : " + infos1.Format;
			var bFailToLoad = new bool();
			txtNormal = "Normal map : " + AssumeNormalMap(sFilePath, ref bFailToLoad);
		}

		//Guess if texture is a normal map
		public static bool AssumeNormalMap(string sFilePath, ref bool bFailToLoad)
		{
			var bNorm = DetectNormalFromTextureName(sFilePath);
			//todo: find a proper way to determine if a texture is a normalmap
			if(!bNorm)
				bNorm = DetectNormalFromTexturePattern(sFilePath, ref bFailToLoad);
			return bNorm;
		}

		private static readonly string[] normalFileNames = { "nrm", "nm", "normal" };

		//check the texture filename to determine if it's a normalmap
		private static bool DetectNormalFromTextureName(string sFilePath)
		{
			var fileName = Path.GetFileName(sFilePath);

			return normalFileNames.Any(name => fileName != null && CultureInfo.CurrentCulture.CompareInfo.IndexOf(fileName, name, CompareOptions.IgnoreCase) >= 0);
		}
		//there is probably a better way to do that... Doing this with DX is still faster than using a bitmap, anyway...good enough...
		private static bool DetectNormalFromTexturePattern(string sFilePath, ref bool bFailToLoad)
		{
			//the pattern we are looking for is actually "is blue channel always 0xFF?", this function return true if it does, false otherwise
			//It assume a file loaded from a tga, since normalmaps are not usually saved as pngs
			var bBool = true;
			try
			{
				var t = TextureLoader.FromFile(dev, sFilePath);
				var gs1 = TextureLoader.SaveToStream(ImageFileFormat.Tga, t);
				var buff = new byte[gs1.Length];
				gs1.Read(buff, 0, (int)gs1.Length);
				var ii = TextureLoader.ImageInformationFromFile(sFilePath);
				var iStep = 3;
				if(ii.Format == Format.A8R8G8B8)
					iStep = 4;
				var startIndex = 0x1f;
				while(startIndex < gs1.Length)
				{
					if(buff[startIndex] != 0xff)
					{
						bBool = false;
						startIndex = (int)gs1.Length;
					}
					startIndex += iStep;
				}
				t.Dispose();
				gs1.Dispose();
				gs1.Close();
			}
			catch(Exception)
			{
				bFailToLoad = true;
				MainForm.Instance.Log_WriteLine("ERR : Can't read " + sFilePath + ", skipping file.");
			}
			return bBool;
		}

		//retrieve informations about a file from it's path
		public static void GetImageInfo(string sFilePath, out string sFormat, out int iWidht, out int iHeight)
		{
			var ii = TextureLoader.ImageInformationFromFile(sFilePath);
			sFormat = ii.Format.ToString();
			iWidht = ii.Width;
			iHeight = ii.Height;
		}

		//function to check if the file use a 32 or a 24-bits palette
		public static bool Is32BPP(ImageInformation iImageInfos)
		{
			return bppFormats32.Contains(iImageInfos.Format);
		}

		public static bool Is32BPP(string sFilePath)
		{
			var iImageInfos = TextureLoader.ImageInformationFromFile(sFilePath);
			return bppFormats32.Contains(iImageInfos.Format);
		}

		//convert a non-mbm file to dds

		public static void ConvertFileToDDS(ConversionParameters convParams, FolderProcessingParams cfg)
		{
			//test: to avoid memory leak, reset the device before every conversion... As this program can't compile on for x64 OSs, large texture hit memory lmit quite fast...
			//there's a known bug with DirectX9 and his way to load TGA files, image buffer is kept in memory even when the texture is disposed. And, of course, .NET's GarbageCollector is not useful AT ALL here...
			ResetDevice();
			/////
			//normals. Since the AssumeNormalMap() function is able to determine if a file failed to load, this function should be called before anything else
			var bNormal = false;
			var bLoadFail = false;

			switch(convParams.NormalMapConversion)
			{
				case NormalMapConversion.Automatic:
					bNormal = AssumeNormalMap(convParams.FilePath, ref bLoadFail);
					break;
				case NormalMapConversion.ForceNormal:
					bNormal = true;
					break;
			}

			//skip the file if it failed to load
			if(bLoadFail)
				return;

			//get a new filepath for our newly created dds
			var fpNew = convParams.FilePath.Remove(convParams.FilePath.Length - 4, 4) + ".dds";
			//retrive infos from the original file
			var iInfos = TextureLoader.ImageInformationFromFile(convParams.FilePath);

			//resize ratio
			var dRatio = cfg.ResizeRatio;
			if(convParams.ResizeSetting == ResizeSetting.DontAllowResize)
				dRatio = 1;

			//Mipmaps level
			var mipmapLevel = 0;
			if(convParams.MipmapSetting == MipmapSetting.DontGenerate)
				mipmapLevel = 1;

			//Output format
			var form = default(Format);

			switch(convParams.OutputFormat)
			{
				case OutputFormat.AutoUncompressed:
					if(bNormal)
						form = Format.A8R8G8B8;
					else
						form = Is32BPP(iInfos) ? Format.A8R8G8B8 : Format.R8G8B8;
					break;
				case OutputFormat.AutoCompressed:
					if(bNormal)
						form = Format.Dxt5;
					else
						form = Is32BPP(iInfos) ? Format.Dxt5 : Format.Dxt1;
					break;
				case OutputFormat.ForceArgb8:
					form = Format.A8R8G8B8;
					break;
				case OutputFormat.ForceRgb8:
					form = Format.R8G8B8;
					break;
				case OutputFormat.ForceDXT5:
					form = Format.Dxt5;
					break;
				case OutputFormat.ForceDXT1:
					form = Format.Dxt1;
					break;
			}

			//info for log
			MainForm.Instance.Log_WriteLine("LOG : Converting " + convParams.FilePath + " to " + fpNew);

			//keep small images uncompressed. This is independent from any parameters, the value of 64 is hard-coded
			if(((iInfos.Width < MinHeightForCompressed) | (iInfos.Height < MinHeightForCompressed)))
			{
				if(((form == Format.Dxt1) | (form == Format.Dxt5)))
					MainForm.Instance.Log_WriteLine("LOG : Resolution is considered too low for DXT compression. Switching to uncompressed format for better quality.");
				form = Format.A8R8G8B8;
				mipmapLevel = 1;
			}


			//skip file if the resolution is not correct
			if(((iInfos.Width < cfg.MinRes_Process_Width) | (iInfos.Height < cfg.MinRes_Process_Width)))
			{
				MainForm.Instance.Log_WriteLine("LOG : Skipping " + convParams.FilePath + ", resolution is too low.");
				FolderLoader.FileSkipCount += 1;
				return;
			}

			//check if the file is large enough to be resized
			if((((iInfos.Width < cfg.MinRes_Resize_Width) | (iInfos.Height < cfg.MinRes_Resize_Height)) & (Math.Abs(dRatio - 1) > 0.0001f)))
			{
				MainForm.Instance.Log_WriteLine("LOG : " + convParams.FilePath.Split('.').Last() + ", resolution is too low to be resized.");
				dRatio = 1;
			}

			//corrected width and height, we want them to be multiple of 4
			var iCorWidth = iInfos.Width;
			var iCorHeight = iInfos.Height;

			if(((iCorWidth % 4) != 0))
				iCorWidth += (4 - (iCorWidth % 4));
			if(((iCorHeight % 4) != 0))
				iCorHeight += (4 - (iCorHeight % 4));

			//bmp format for flipping
			Texture texture;
			if(bbpFormats24.Contains(iInfos.Format))
			{
				texture = TextureLoader.FromFile(dev, convParams.FilePath, iCorWidth, iCorHeight, 1, Usage.None, Format.R8G8B8, Pool.SystemMemory, Filter.None, Filter.None,
				0);
				//t = TextureLoader.FromFile(dev, convParams.FilePath, iCorWidth, iCorHeight, 1, Usage.None, form, Pool.SystemMemory, Filter.None, Filter.None, 0)
			}
			else if(bppFormats32.Contains(iInfos.Format))
			{
				texture = TextureLoader.FromFile(dev, convParams.FilePath, iCorWidth, iCorHeight, 1, Usage.None, Format.A8B8G8R8, Pool.SystemMemory, Filter.None, Filter.None,
				0);
			}
			else
			{
				MainForm.Instance.Log_WriteLine("ERR : Unknown file format " + iInfos.Format + ", skipping conversion for " + convParams.FilePath);
				FolderLoader.FileSkipCount += 1;
				return;
			}

			//info for log
			MainForm.Instance.Log_WriteLine("    Format : " + form + ", normalmap : " + bNormal + ", res: " + (iCorWidth * dRatio) + "x" + (iCorHeight * dRatio));

			//the output is saved in a graphicStream, but a memorystream could work just as well.
			var gs = TextureLoader.SaveToStream(ImageFileFormat.Bmp, texture);
			texture.Dispose();

			//flip
			FlipImage(gs);

			//switch to tga format if swizzling 
			if(bNormal)
			{
				texture.Dispose();
				texture = TextureLoader.FromStream(dev, gs);
				gs.Close();
				gs.Dispose();
				gs = TextureLoader.SaveToStream(ImageFileFormat.Tga, texture);
				texture.Dispose();
				SwizzleImage(gs, iCorWidth, iCorHeight, Is32BPP(iInfos));
			}

			//another attempt to flush memory: the program tend to crash if too much large textures are converted
			//though, if some smaller textures are converted in between, memory is flushed correctly...
			dev.Reset(pParameters);

			//saving the texture
			texture = TextureLoader.FromStream(dev, gs, (int) (iCorWidth * dRatio), (int) (iCorHeight * dRatio), mipmapLevel, Usage.None, form, Pool.SystemMemory, Filter.Triangle | Filter.DitherDiffusion, Filter.Triangle | Filter.DitherDiffusion,
			0);
			TextureLoader.Save(fpNew, ImageFileFormat.Dds, texture);
			//delete unused stuff
			texture.Dispose();
			gs.Close();
			gs.Dispose();

			//set flag for normals
			if(bNormal)
				MarkAsNormal(fpNew);

			//remove/Backup original file after successful conversion
			if(File.Exists(fpNew))
			{
				if(cfg.BackupFile)
				{
					File.Move(convParams.FilePath, convParams.FilePath + ".ddsified");
				}
				else if(cfg.DeleteFilesOnSuccess)
				{
					File.Delete(convParams.FilePath);
				}
			}

		}

		//Convert a mbm file to dds
		public static void ConvertMbmtoDDS(ConversionParameters convParams, FolderProcessingParams cfg)
		{
			//get a new filepath with a dds extension
			var fpNew = convParams.FilePath.Remove(convParams.FilePath.Length - 4, 4) + ".dds";

			//open a new mbm file
			var mbmTemp = new MBMLoader.MBMFile(convParams.FilePath);

			//resize ratio
			var dRatio = cfg.ResizeRatio;
			if(convParams.ResizeSetting == ResizeSetting.DontAllowResize)
				dRatio = 1;

			//mipmaps level
			var mipmapLevel = 0;
			if(convParams.MipmapSetting == MipmapSetting.DontGenerate)
				mipmapLevel = 1;


			//output format
			var form = default(Format);
			switch(convParams.OutputFormat)
			{
				case OutputFormat.AutoUncompressed:
					if(mbmTemp.IsNormal)
						form = Format.A8R8G8B8;
					else
						form = mbmTemp.ColorDepth == 24 ? Format.R8G8B8 : Format.A8R8G8B8;
					break;
				case OutputFormat.AutoCompressed:
					form = mbmTemp.ColorDepth == 24 ? Format.Dxt1 : Format.Dxt5;
					break;
				case OutputFormat.ForceArgb8:
					form = Format.A8R8G8B8;
					break;
				case OutputFormat.ForceRgb8:
					form = Format.R8G8B8;
					break;
				case OutputFormat.ForceDXT5:
					form = Format.Dxt5;
					break;
				case OutputFormat.ForceDXT1:
					form = Format.Dxt1;
					break;
			}

			//force normal, or automatic detection
			var bNormal = false;
			switch(convParams.NormalMapConversion)
			{
				case NormalMapConversion.Automatic:
					bNormal = mbmTemp.IsNormal;
					break;
				case NormalMapConversion.ForceNormal:
					bNormal = true;
					break;
			}

			//conversion
			var gs = new MemoryStream();
			mbmTemp.AsBitmap().Save(gs, ImageFormat.Bmp);
			MainForm.Instance.Log_WriteLine("LOG : Converting " + convParams.FilePath + " to " + fpNew);
			MainForm.Instance.Log_WriteLine("    Format : " + form + ", normalmap : " + bNormal + ", res: " + (mbmTemp.Width * dRatio) + "x" + (mbmTemp.Height * dRatio));
			if((((mbmTemp.Width < cfg.MinRes_Resize_Width) | (mbmTemp.Height < cfg.MinRes_Resize_Height)) & (Math.Abs(dRatio - 1) > 0.0001f)))
			{
				MainForm.Instance.Log_WriteLine("LOG : " + convParams.FilePath.Split('.').Last() + ", resolution is too low to be resized.");
				dRatio = 1;
			}
			gs.Position = 0;
			var t = TextureLoader.FromStream(dev, gs, (int) (mbmTemp.Width * dRatio), (int) (mbmTemp.Height * dRatio), mipmapLevel, Usage.None, form, Pool.SystemMemory, Filter.Triangle | Filter.Dither, Filter.Triangle | Filter.Dither,
			0);
			gs.Close();
			gs.Dispose();
			TextureLoader.Save(fpNew, ImageFileFormat.Dds, t);

			//set flag for dxt5nm
			if(bNormal)
				MarkAsNormal(fpNew);

			//flush textures
			t.Dispose();
			mbmTemp.Flush();

			//delete file after successful conversion
			if(File.Exists(fpNew))
			{
				if(cfg.BackupFile)
					File.Move(convParams.FilePath, convParams.FilePath + ".ddsified");
				else if(cfg.DeleteFilesOnSuccess)
					File.Delete(convParams.FilePath);
			}
		}

		//flip the image.
		private static void FlipImage(GraphicsStream gs)
		{
			gs.Position = 0;
			var img = Image.FromStream(gs, true);
			img.RotateFlip(RotateFlipType.RotateNoneFlipY);
			img.Save(gs, ImageFormat.Bmp);
			img.Dispose();
			gs.Position = 0;
		}

		//Swizzlin'
		private static void SwizzleImage(GraphicsStream gs, int iWidth, int iHeight, bool b32BPP)
		{
			//since swizzling is only for normals, and those must be saved in dxt5 (or argb8), might as well ensure the texture is in the correct format.
			if(!b32BPP)
			{
				var tTemp = TextureLoader.FromStream(dev, gs, iWidth, iHeight, 1, Usage.None, Format.A8B8G8R8, Pool.SystemMemory, Filter.None, Filter.None,
				0);
				gs = TextureLoader.SaveToStream(ImageFileFormat.Tga, tTemp);
				tTemp.Dispose();
			}

			var bOri = new byte[gs.Length];
			//byte array from original stream
			var bSwizzled = new byte[gs.Length];
			//swizzled array
			gs.Read(bOri, 0, (int)gs.Length);
			const int headerSize = 0x1f;
			//a header for a tga file is usually 18bytes long, but for a reason I don't get, here it start at 0x1F...
			// Dim headerSize As Integer = 18
			for(var i = 0; i <= headerSize - 1; i++)
			{
				bSwizzled[i] = bOri[i];
			}
			//there's probably a better way to do that, but this one is self-explanatory...
			for(var y = 0; y <= iHeight - 1; y++)
			{
				for(var x = 0; x <= iWidth - 1; x++)
				{
					var pos = (((y * iWidth) + x) * 4) + headerSize;

					//b = bOri(pos)
					var g = bOri[pos + 1];
					var r = bOri[pos + 2];
					//a = bOri(pos + 3)

					bSwizzled[pos] = 255;
					bSwizzled[pos + 1] = g;
					bSwizzled[pos + 2] = 255;
					bSwizzled[pos + 3] = r;
				}
			}
			gs.Position = 0;
			gs.Write(bSwizzled, 0, bSwizzled.Length);
			gs.Position = 0;
		}

		//set the "normal" flag
		public static void MarkAsNormal(string sDDSFilePath)
		{
			//I could write a proper class to read DDS header and set all arguments properly, but this works just as well, and it's quite fast
			var fs = File.Open(sDDSFilePath, FileMode.Open, FileAccess.ReadWrite);
			fs.Position = 0x53;
			var b = (byte)fs.ReadByte();
			fs.Position = 0x53;
			fs.WriteByte((byte)(b | 0x80));
			fs.Close();
		}

		//will determinate texture creation parameters from a file (used only in the single-file conversion window)
		public class AutoParameters
		{
			private readonly int width;
			private readonly int height;
			private readonly bool normal;
			private readonly string format;

			public Size Resolution
			{
				get { return new Size(width, height); }
			}
			public string FileName
			{
				get { return Path.GetFileName(FilePath); }
			}

			public string FilePath { get; private set; }

			public string Format
			{
				get { return format; }
			}
			public bool NormalMap
			{
				get { return normal; }
			}
			public AutoParameters(string filePath)
			{
				FilePath = filePath;
				
				if(Path.GetExtension(filePath) == ".mbm")
					MBMLoader.GetInfo(filePath, out width, out height, out normal, out format);
				else
				{
					var ii = TextureLoader.ImageInformationFromFile(filePath);
					width = ii.Width;
					height = ii.Height;
					format = ii.Format.ToString();
					var bFail = false;
					normal = AssumeNormalMap(filePath, ref bFail);

					if(bFail)
						normal = false;
				}
			}
		}

		//File creation parameters, used with ConvertFileToDDS() and ConvertMBMToDDS()
		public class ConversionParameters
		{
			//store infos, I could have go with integers and boolean directly to store parameters instead of using enums, but it's easier to read this way

			private string filepath;
			public NormalMapConversion NormalMapConversion { get; set; }
			public MipmapSetting MipmapSetting { get; set; }
			public ResizeSetting ResizeSetting { get; set; }
			public OutputFormat OutputFormat { get; set; }

			public string FilePath
			{
				get { return filepath; }
				set { filepath = value; }
			}

			public FileType FileType { get; set; }

			//basic constructor. Default params means "all automatic"
			public ConversionParameters(string sFilePath)
			{
				filepath = sFilePath;
				FileType = Path.GetExtension(filepath) == ".mbm" ? FileType.MBM : FileType.Standard;
				NormalMapConversion = NormalMapConversion.Automatic;
				MipmapSetting = MipmapSetting.Generate;
				ResizeSetting = ResizeSetting.AllowResize;
				OutputFormat = OutputFormat.AutoCompressed;
			}

			//create with parameters
			public ConversionParameters(string sFilePath, NormalMapConversion eNormal, MipmapSetting eMipmaps, ResizeSetting eResize, OutputFormat eOutput)
			{
				filepath = sFilePath;
				FileType = Path.GetExtension(filepath) == ".mbm" ? FileType.MBM : FileType.Standard;
				NormalMapConversion = eNormal;
				MipmapSetting = eMipmaps;
				ResizeSetting = eResize;
				OutputFormat = eOutput;
			}
		}

	}
}