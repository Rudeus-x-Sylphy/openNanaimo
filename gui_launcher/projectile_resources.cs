// Small optional resource installer helper; compatible with Windows PowerShell 5.1.
using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
namespace Nanaimo.Projectile {
public static class Bytes {
    public static byte[] Hex(string s) {
        if(s.Length%2!=0)throw new InvalidDataException("Odd hex length");
        byte[] b=new byte[s.Length/2];for(int i=0;i<b.Length;i++)b[i]=Convert.ToByte(s.Substring(i*2,2),16);return b;
    }
    public static string Hash(byte[] b){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(b)).Replace("-","");}
    public static bool Equal(byte[] a,byte[] b){if(a==null||b==null)return a==b;if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
    static int U16(byte[] b,int p){if(p<0||p>b.Length-2)throw new InvalidDataException("Truncated PE");return BitConverter.ToUInt16(b,p);}
    static int U32(byte[] b,int p){if(p<0||p>b.Length-4)throw new InvalidDataException("Truncated PE");return checked((int)BitConverter.ToUInt32(b,p));}
    static int Offset(byte[] b,int va,int length){
        if(b.Length<64||b[0]!=77||b[1]!=90)throw new InvalidDataException("Invalid DOS header");
        int pe=U32(b,60);if(U32(b,pe)!=0x4550||U16(b,pe+4)!=0x14c||U16(b,pe+24)!=0x10b)throw new InvalidDataException("Expected x86 PE32");
        int count=U16(b,pe+6),size=U16(b,pe+20),imageBase=U32(b,pe+52),result=-1;
        if(count<1||count>96||size<96)throw new InvalidDataException("Invalid PE sections");
        for(int i=0;i<count;i++){
            int row=checked(pe+24+size+i*40),rva=U32(b,row+12),rawSize=U32(b,row+16),raw=U32(b,row+20);
            if(raw>b.Length||rawSize>b.Length-raw)throw new InvalidDataException("Truncated PE section");
            long delta=(long)va-imageBase-rva;
            if(delta>=0&&delta+length<=rawSize){if(result!=-1)throw new InvalidDataException("Ambiguous PE mapping");result=checked(raw+(int)delta);}
        }
        if(result<0)throw new InvalidDataException("Unmapped patch VA");return result;
    }
    public static void Validate(byte[] b){
        int pe=U32(b,60),baseAddress=U32(b,pe+52);
        if(baseAddress!=0x400000||(U16(b,pe+24+70)&0x40)!=0)throw new InvalidDataException("Unsupported image base/ASLR contract");
        int count=U16(b,pe+6),size=U16(b,pe+20);bool writable=false;
        for(int i=0;i<count;i++){
            int row=checked(pe+24+size+i*40),rva=U32(b,row+12),rawSize=U32(b,row+16);
            if(rva<=0x798000&&0x79A000L<=rva+(long)rawSize){
                uint flags=BitConverter.ToUInt32(b,row+36);writable=(flags&0xE0000000)==0xE0000000;
            }
        }
        if(!writable)throw new InvalidDataException("DIY cave must be readable/writable/executable");
        int[] slots={0xD921E8,0xD91FAC,0xD91EA8,0xD91F50,0xD91ED0};
        string[] names={"GetKeyState","GetTickCount","GetPrivateProfileStringA","GetModuleFileNameA","GetFileAttributesA"};
        for(int i=0;i<slots.Length;i++){
            int rva=U32(b,Offset(b,slots[i],4)),at=Offset(b,checked(baseAddress+rva+2),names[i].Length+1);
            if(Encoding.ASCII.GetString(b,at,names[i].Length)!=names[i]||b[at+names[i].Length]!=0)
                throw new InvalidDataException("Unsupported DIY WinAPI import slot");
        }
    }
    public static void Patch(byte[] b,int va,byte[] oldBytes,byte[] newBytes,bool remove){
        if(oldBytes.Length!=newBytes.Length)throw new InvalidDataException("Patch span differs");
        int at=Offset(b,va,oldBytes.Length);byte[] current=new byte[oldBytes.Length];Buffer.BlockCopy(b,at,current,0,current.Length);
        if(!Equal(current,oldBytes)&&!Equal(current,newBytes))throw new InvalidDataException("Unreviewed client bytes at "+va.ToString("X"));
        Buffer.BlockCopy(remove?oldBytes:newBytes,0,b,at,current.Length);
    }
    public static byte[] BasketballPon(byte[] original){
        if(Hash(original)!="48BEEEE6450507CB3970E87BA8FEC124C6D8968CA3BE3C7EC975AA4EB64A2597")throw new InvalidDataException("Unreviewed original basketball PON");
        byte[] b=(byte[])original.Clone(),name=Encoding.ASCII.GetBytes("nanaimo_basketball.eff");
        for(int i=0;i<260;i++)b[4+i]=(byte)((i<name.Length?name[i]:0)^0xAB);return b;
    }
    public static byte[] BasketballEffImageOnly(byte[] original){
        if(Hash(original)!="6C3400DDA86BE221387688C49A5E03296F748E8247AF040865F84B422598BCBD")throw new InvalidDataException("Unreviewed original basketball EFF");
        byte[] b=(byte[])original.Clone(),name=Encoding.ASCII.GetBytes("nanaimo_basketball.im3");
        for(int i=0;i<256;i++)b[28+i]=i<name.Length?name[i]:i==name.Length?(byte)0:(byte)0xCC;return b;
    }
    public static byte[] BasketballEff(byte[] original){
        byte[] b=BasketballEffImageOnly(original);
        // Preserve all three PON/EFF rows and timing. Only the basketball is
        // visible: original muzzle burst layers keep their objects but alpha=0.
        // Ball alpha=255 from birth avoids retaining the old muzzle-flash fade-in.
        if(U32(b,0)!=2||U32(b,16)!=3)throw new InvalidDataException("Unexpected basketball EFF layout");
        int offset=20;
        for(int row=0;row<3;row++){
            offset=checked(offset+268);
            for(int track=0;track<12;track++){
                int count=U32(b,offset);offset=checked(offset+4);
                if(count<1||count>(b.Length-offset)/16)throw new InvalidDataException("Invalid basketball EFF curve");
                if(track==7){
                    for(int k=0;k<count;k++){
                        Buffer.BlockCopy(BitConverter.GetBytes(row==0?255f:0f),0,b,offset+k*16,4);
                        Array.Clear(b,offset+k*16+4,4);
                    }
                }
                offset=checked(offset+count*16);
            }
            offset=checked(offset+48);
        }
        if(offset!=b.Length)throw new InvalidDataException("Unexpected basketball EFF trailing bytes");
        return b;
    }
    public static void AtomicWrite(string path,byte[] bytes){
        Directory.CreateDirectory(Path.GetDirectoryName(path));string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(bytes,0,bytes.Length);f.Flush(true);}if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);}finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
}}
