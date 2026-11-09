using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// PWG 5102.4: big-endian page headers and PackBits-like row compression.
public static class PwgRaster {
    static byte[] Exact(Stream s,int n) {
        byte[] data=new byte[n]; int pos=0,count;
        while(pos<n && (count=s.Read(data,pos,n-pos))>0) pos+=count;
        if(pos!=n) throw new InvalidDataException("Truncated PWG raster");
        return data;
    }
    static int Byte(Stream s) { int value=s.ReadByte(); if(value<0) throw new InvalidDataException("Truncated PWG row"); return value; }
    static int Int(byte[] h,int p) {
        uint value=((uint)h[p]<<24)|((uint)h[p+1]<<16)|((uint)h[p+2]<<8)|h[p+3];
        if(value>int.MaxValue) throw new InvalidDataException("Invalid PWG header integer");
        return (int)value;
    }
    public static int Convert(string source,string dest) {
        using(FileStream input=File.OpenRead(source))
        using(FileStream output=new FileStream(dest,FileMode.CreateNew)) {
            if(Encoding.ASCII.GetString(Exact(input,4))!="RaS2") throw new InvalidDataException("Expected PWG RaS2 signature");
            int pages=0;
            while(input.Position<input.Length) {
                if(++pages>100) throw new InvalidDataException("Maximum 100 pages per job");
                byte[] h=Exact(input,1796);
                if(Encoding.ASCII.GetString(h,0,9)!="PwgRaster") throw new InvalidDataException("Invalid PWG header");
                int width=Int(h,372),height=Int(h,376),bits=Int(h,388),bpc=Int(h,384),lineBytes=Int(h,392),color=Int(h,400);
                if(width<1||height<1||width>7104||height>7104||(long)width*height>36000000)
                    throw new InvalidDataException("PWG page exceeds A4 / 600 dpi limits");
                if(Int(h,276)!=600||Int(h,280)!=600||Int(h,396)!=0)
                    throw new InvalidDataException("PWG requires 600 dpi and chunky color order");
                if(!((bits==1&&bpc==1&&(color==3||color==18))||(bits==8&&bpc==8&&(color==3||color==18))||(bits==24&&bpc==8&&(color==1||color==19))))
                    throw new InvalidDataException("Unsupported PWG color space or bit depth");
                int expected=(width*bits+7)/8;
                if(lineBytes!=expected) throw new InvalidDataException("Invalid PWG scanline size");
                int unit=Math.Max(1,bits/8);
                byte[] row=new byte[lineBytes];
                using(Bitmap bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb)) {
                    bitmap.SetResolution(600,600);
                    BitmapData data=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
                    try {
                        byte[] rgb=new byte[data.Stride];
                        for(int y=0;y<height;) {
                            int repeat=Byte(input)+1;
                            if(y+repeat>height) throw new InvalidDataException("PWG row repetition overflow");
                            for(int pos=0;pos<lineBytes;) {
                                int code=Byte(input);
                                if(code==128) throw new InvalidDataException("Invalid PWG run-length code");
                                int count=(code<128?code+1:257-code)*unit;
                                if(pos+count>lineBytes) throw new InvalidDataException("PWG row run overflow");
                                if(code<128) {
                                    byte[] pixel=Exact(input,unit);
                                    for(int i=0;i<count;i++) row[pos+i]=pixel[i%unit];
                                } else {
                                    byte[] literal=Exact(input,count); Buffer.BlockCopy(literal,0,row,pos,count);
                                }
                                pos+=count;
                            }
                            for(int x=0;x<width;x++) {
                                if(bits==24) { rgb[x*3]=row[x*3+2];rgb[x*3+1]=row[x*3+1];rgb[x*3+2]=row[x*3]; }
                                else {
                                    int gray=bits==1?(((row[x/8]>>(7-x%8))&1)*255):row[x];
                                    if(color==3) gray=255-gray;
                                    rgb[x*3]=rgb[x*3+1]=rgb[x*3+2]=(byte)gray;
                                }
                            }
                            for(int r=0;r<repeat;r++,y++) Marshal.Copy(rgb,0,IntPtr.Add(data.Scan0,y*data.Stride),rgb.Length);
                        }
                    } finally { bitmap.UnlockBits(data); }
                    CaptRaster.WritePbm(bitmap,output);
                }
            }
            if(pages==0) throw new InvalidDataException("PWG contains no pages");
            return pages;
        }
    }
}
