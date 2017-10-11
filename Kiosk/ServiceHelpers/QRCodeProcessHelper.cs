using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace ServiceHelpers
{
    public static class QRCodeProcessHelper
    {
        public static QRCodeReader reader = new QRCodeReader();
        public async static Task<bool>  IdentifyQRCode(byte[] buffer,int width, int height)
        {
                await Task.Delay(300);
                
                LuminanceSource source = new RGBLuminanceSource(buffer, width, height);
                var binarizer = new HybridBinarizer(source);
                var binBitmap = new BinaryBitmap(binarizer);

                Result result = reader.decode(binBitmap);

            
            if (result != null)
                {
                    
                    return true;
                }
            else
                return false;
        }
    }
}
