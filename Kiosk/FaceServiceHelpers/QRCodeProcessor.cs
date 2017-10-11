using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using ZXing;

namespace FaceServiceHelpers
{
    public class QRCodeProcessor
    {
        private BarcodeReader barcodeReader;
        private Result result;
        private QRCodeCoordinates qrCodeCoordinates;

        public QRCodeProcessor()
        {
            barcodeReader = new BarcodeReader();
            barcodeReader.Options.PureBarcode = false;
            barcodeReader.Options.Hints.Add(DecodeHintType.TRY_HARDER, true);
            barcodeReader.Options.PossibleFormats =
              new BarcodeFormat[] { BarcodeFormat.QR_CODE };
            barcodeReader.Options.TryHarder = true;
        }
        public DetectedQRCode DecodeQRCodes(SoftwareBitmap bitmap)
        {
            this.result = barcodeReader.Decode(bitmap);
            DetectedQRCode qRCode = null;
            if (this.result != null)
            {
                qrCodeCoordinates = new QRCodeCoordinates(this.result.ResultPoints);
                qRCode = qrCodeCoordinates.GetDetectedQRCode();
                qRCode.QRCodeResult = this.result;
            }
            return qRCode;
        }       
    }

    public class QRCodeCoordinates
    {
        public float X1 { get; set; }
        public float X2 { get; set; }
        public float Y1 { get; set; }
        public float Y2 { get; set; }

        public float X3 { get; set; }
        public float Y3 { get; set; }


        public QRCodeCoordinates(ZXing.ResultPoint[] resultPoints)
        {
            this.X1 = resultPoints[0].X; // index 0: bottom left
            this.X2 = resultPoints[2].X; // index 2: top right
            this.Y1 = resultPoints[2].Y; // index 2: top right
            this.Y2 = resultPoints[0].Y; // index 0: bottom left  

            this.X3 = resultPoints[1].X; // index 0: top left  
            this.Y3 = resultPoints[1].Y; // index 0: top left  
        }
        public DetectedQRCode GetDetectedQRCode()
        {
            DetectedQRCode detectedQRCode = new DetectedQRCode();
            BitmapBounds bounds = new BitmapBounds()
            {
                X = (uint)this.X1,
                Y = (uint)this.Y1,
                Width = (uint)Math.Abs(X2 - X1),
                Height = (uint)Math.Abs(Y2 - Y1)
            };
            detectedQRCode.QRCodeBox = bounds;

            return detectedQRCode;
        }
    }
    public class DetectedQRCode
    {
        public BitmapBounds QRCodeBox { get; set; }
        public Result QRCodeResult { get; set; }
    }
}
