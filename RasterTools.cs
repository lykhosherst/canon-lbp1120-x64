using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class CaptRaster {
    public const int Width=4800, Height=6768;
    public static void WritePbm(Image image, Stream output) {
        using (Bitmap canvas=new Bitmap(Width,Height,PixelFormat.Format24bppRgb)) {
            canvas.SetResolution(600,600);
            using (Graphics g=Graphics.FromImage(canvas)) {
                g.Clear(Color.White);
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                double scale=Math.Min((double)Width/image.Width,(double)Height/image.Height);
                int w=(int)Math.Round(image.Width*scale),h=(int)Math.Round(image.Height*scale);
                g.DrawImage(image,new Rectangle((Width-w)/2,(Height-h)/2,w,h));
            }
            BitmapData data=canvas.LockBits(new Rectangle(0,0,Width,Height),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
            try {
                byte[] pixels=new byte[data.Stride*Height];
                Marshal.Copy(data.Scan0,pixels,0,pixels.Length);
                byte[] raster=new byte[Width/8*Height];
                int[] bayer={0,8,2,10,12,4,14,6,3,11,1,9,15,7,13,5};
                for(int y=0;y<Height;y++) for(int x=0;x<Width;x++) {
                    int p=y*data.Stride+x*3;
                    int gray=(pixels[p]*29+pixels[p+1]*150+pixels[p+2]*77)>>8;
                    int threshold=bayer[(y%4)*4+x%4]*16+8;
                    if(gray<threshold) raster[y*(Width/8)+x/8]|=(byte)(0x80>>(x%8));
                }
                byte[] header=Encoding.ASCII.GetBytes("P4\n"+Width+" "+Height+"\n");
                output.Write(header,0,header.Length);
                output.Write(raster,0,raster.Length);
            } finally {canvas.UnlockBits(data);}
        }
    }
    public static void TestPage(string path) {
        using(Bitmap image=new Bitmap(Width,Height)) {
            image.SetResolution(600,600);
            using(Graphics g=Graphics.FromImage(image))
            using(Font title=new Font("Arial",110,FontStyle.Bold,GraphicsUnit.Pixel))
            using(Font text=new Font("Arial",65,FontStyle.Regular,GraphicsUnit.Pixel))
            using(Pen border=new Pen(Color.Black,4)) {
                g.Clear(Color.White);
                g.DrawRectangle(border,100,100,Width-200,Height-200);
                g.DrawString("Canon LBP-1120",title,Brushes.Black,220,250);
                g.DrawString("Windows 11 x64 - native CAPT test",text,Brushes.Black,220,450);
                g.DrawString("600 dpi / A4 / usbprint.sys / libcapt",text,Brushes.Black,220,570);
                g.DrawString("If this page is readable, USB + raster printing works.",text,Brushes.Black,220,780);
                g.DrawString("This test does not install a Windows printer driver.",text,Brushes.Black,220,900);
                for(int i=0;i<8;i++) using(Brush gray=new SolidBrush(Color.FromArgb(i*255/7,i*255/7,i*255/7)))
                    g.FillRectangle(gray,220+i*500,1200,500,500);
                for(int i=0;i<20;i++) g.DrawLine(border,220,2000+i*25,Width-220,2000+i*25);
                g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),text,Brushes.Black,220,Height-400);
            }
            using(FileStream output=File.Create(path)) WritePbm(image,output);
        }
    }
}
