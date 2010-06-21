// 
// OraFormat.cs
//  
// Author:
//       Maia Kozheva <sikon@ubuntu.com>
// 
// Copyright (c) 2010 Maia Kozheva <sikon@ubuntu.com>
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.IO;
using System.Xml;

using Gtk;
using Gdk;
using Cairo;

using ICSharpCode.SharpZipLib.Zip;

namespace Pinta.Core
{
	public class OraFormat: IImageImporter, IImageExporter
	{
		private const int ThumbMaxSize = 256;
		
		public void Import (LayerManager layers, string fileName) {
			ZipFile file = new ZipFile (fileName);
			XmlDocument stackXml = new XmlDocument ();
			stackXml.Load (file.GetInputStream (file.GetEntry ("stack.xml")));
			
			XmlElement imageElement = stackXml.DocumentElement;
			int width = int.Parse (imageElement.GetAttribute ("w"));
			int height = int.Parse (imageElement.GetAttribute ("h"));
			
			XmlElement stackElement = (XmlElement) stackXml.GetElementsByTagName ("stack")[0];
			XmlNodeList layerElements = stackElement.GetElementsByTagName ("layer");
			
			if (layerElements.Count == 0)
				throw new XmlException ("No layers found in OpenRaster file");

			layers.Clear ();
			PintaCore.History.Clear ();
			layers.DestroySelectionLayer ();
			PintaCore.Workspace.ImageSize = new Size (width, height);
			PintaCore.Workspace.CanvasSize = new Gdk.Size (width, height);

			for (int i = 0; i < layerElements.Count; i++) {
				XmlElement layerElement = (XmlElement) layerElements[i];
				int x = int.Parse (GetAttribute (layerElement, "x", "0"));
				int y = int.Parse (GetAttribute (layerElement, "y", "0"));
				string name = GetAttribute (layerElement, "name", string.Format ("Layer {0}", i));
				
				try {
					// Write the file to a temporary file first
					// Fixes a bug when running on .Net
					ZipEntry zf = file.GetEntry (layerElement.GetAttribute ("src"));
					Stream s = file.GetInputStream (zf);
					string tmp_file = System.IO.Path.GetTempFileName ();
					
					using (Stream stream_out = File.Open (tmp_file, FileMode.OpenOrCreate)) {
						byte[] buffer = new byte[2048];
						
						while (true) {
							int len = s.Read (buffer, 0, buffer.Length);
							
							if (len > 0)
								stream_out.Write (buffer, 0, len);
							else
								break;					
						}
					}

					Layer layer = layers.CreateLayer (name, width, height);
					layers.Insert (layer, 0);
					layer.Opacity = double.Parse (GetAttribute (layerElement, "opacity", "1"), GetFormat ());
					
					using (Pixbuf pb = new Pixbuf (tmp_file)) {
						using (Context g = new Context (layer.Surface)) {
							CairoHelper.SetSourcePixbuf (g, pb, x, y);
							g.Paint ();
						}
					}
					
					try {
						File.Delete (tmp_file);
					} catch { }
				} catch {
					MessageDialog md = new MessageDialog (PintaCore.Chrome.MainWindow, DialogFlags.Modal, MessageType.Error, ButtonsType.Ok, "Could not import layer \"{0}\" from {0}", name, file);
					md.Title = "Error";
				
					md.Run ();
					md.Destroy ();
				}
			}
			
			file.Close ();
		}
		
		private static IFormatProvider GetFormat () {
			return System.Globalization.CultureInfo.CreateSpecificCulture ("en");
		}

		private static string GetAttribute (XmlElement element, string attribute, string defValue) {
			string ret = element.GetAttribute (attribute);
			return string.IsNullOrEmpty (ret) ? defValue : ret;
		}

		private Size GetThumbDimensions (int width, int height) {
			if (width <= ThumbMaxSize && height <= ThumbMaxSize)
				return new Size (width, height);

			if (width > height)
				return new Size (ThumbMaxSize, (int) ((double)height / width * ThumbMaxSize));
			else
				return new Size ((int) ((double)width / height * ThumbMaxSize), ThumbMaxSize);
		}

		private byte[] GetLayerXmlData (LayerManager layers) {
			MemoryStream ms = new MemoryStream ();
			XmlTextWriter writer = new XmlTextWriter (ms, System.Text.Encoding.UTF8);
			writer.Formatting = Formatting.Indented;

			writer.WriteStartElement ("image");
			writer.WriteAttributeString ("w", layers[0].Surface.Width.ToString ());
			writer.WriteAttributeString ("h", layers[0].Surface.Height.ToString ());

			writer.WriteStartElement ("stack");
			writer.WriteAttributeString ("opacity", "1");
			writer.WriteAttributeString ("name", "root");

			// ORA stores layers top to bottom
			for (int i = layers.Count - 1; i >= 0; i--) {
				writer.WriteStartElement ("layer");
				writer.WriteAttributeString ("opacity", layers[i].Hidden ? "0" : string.Format (GetFormat (), "{0:0.00}", layers[i].Opacity));
				writer.WriteAttributeString ("name", layers[i].Name);
				writer.WriteAttributeString ("src", "data/layer" + i.ToString () + ".png");
				writer.WriteEndElement ();
			}

			writer.WriteEndElement (); // stack
			writer.WriteEndElement (); // image

			writer.Close ();
			return ms.ToArray ();
		}

		public void Export (LayerManager layers, string fileName) {
			ZipOutputStream stream = new ZipOutputStream (new FileStream (fileName, FileMode.Create));
			ZipEntry mimetype = new ZipEntry ("mimetype");
			mimetype.CompressionMethod = CompressionMethod.Stored;
			stream.PutNextEntry (mimetype);

			byte[] databytes = System.Text.Encoding.ASCII.GetBytes ("image/openraster");
			stream.Write (databytes, 0, databytes.Length);

			for (int i = 0; i < layers.Count; i++) {
				Pixbuf pb = layers[i].Surface.ToPixbuf ();
				byte[] buf = pb.SaveToBuffer ("png");
				(pb as IDisposable).Dispose ();

				stream.PutNextEntry (new ZipEntry ("data/layer" + i.ToString () + ".png"));
				stream.Write (buf, 0, buf.Length);
			}

			stream.PutNextEntry (new ZipEntry ("stack.xml"));
			databytes = GetLayerXmlData (layers);
			stream.Write (databytes, 0, databytes.Length);

			ImageSurface flattened = layers.GetFlattenedImage();
			Pixbuf flattenedPb = flattened.ToPixbuf ();
			Size newSize = GetThumbDimensions (flattenedPb.Width, flattenedPb.Height);
			Pixbuf thumb = flattenedPb.ScaleSimple (newSize.Width, newSize.Height, InterpType.Bilinear);

			stream.PutNextEntry (new ZipEntry ("Thumbnails/thumbnail.png"));
			databytes = thumb.SaveToBuffer ("png");
			stream.Write (databytes, 0, databytes.Length);

			(flattened as IDisposable).Dispose();
			(flattenedPb as IDisposable).Dispose();
			(thumb as IDisposable).Dispose();

			stream.Close ();
		}
	}
}
