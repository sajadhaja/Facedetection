// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Cognitive Services: http://www.microsoft.com/cognitive
// 
// Microsoft Cognitive Services Github:
// https://github.com/Microsoft/Cognitive
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// 

using FaceServiceHelpers;
using Microsoft.ProjectOxford.Common;
using Microsoft.ProjectOxford.Common.Contract;
using Microsoft.ProjectOxford.Face.Contract;
using ServiceHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;
using Windows.System.Threading;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;

// The User Control item template is documented at http://go.microsoft.com/fwlink/?LinkId=234236

namespace IntelligentKioskSample.Controls
{
    public enum AutoCaptureState
    {
        WaitingForFaces,
        WaitingForStillFaces,
        ShowingCountdownForCapture,
        ShowingCapturedPhoto
    }

    public interface IRealTimeDataProvider
    {
        EmotionScores GetLastEmotionForFace(BitmapBounds faceBox);
        Face GetLastFaceAttributesForFace(BitmapBounds faceBox);
        IdentifiedPerson GetLastIdentifiedPersonForFace(BitmapBounds faceBox);
        SimilarPersistedFace GetLastSimilarPersistedFaceForFace(BitmapBounds faceBox);
    }

    public sealed partial class CameraControl : UserControl
    {
        public event EventHandler<ImageAnalyzer> ImageCaptured;
        public event EventHandler<AutoCaptureState> AutoCaptureStateChanged;
        public event EventHandler CameraRestarted;
        public event EventHandler CameraAspectRatioChanged;

        public static readonly DependencyProperty ShowDialogOnApiErrorsProperty =
            DependencyProperty.Register(
            "ShowDialogOnApiErrors",
            typeof(bool),
            typeof(CameraControl),
            new PropertyMetadata(true)
            );

        public bool ShowDialogOnApiErrors
        {
            get { return (bool)GetValue(ShowDialogOnApiErrorsProperty); }
            set { SetValue(ShowDialogOnApiErrorsProperty, (bool)value); }
        }

        public bool FilterOutSmallFaces
        {
            get;
            set;
        }

        private bool enableAutoCaptureMode;
        public bool EnableAutoCaptureMode
        {
            get
            {
                return enableAutoCaptureMode;
            }
            set
            {
                this.enableAutoCaptureMode = value;
                this.commandBar.Visibility = this.enableAutoCaptureMode ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        private readonly SolidColorBrush fillBrush = new SolidColorBrush(Windows.UI.Colors.Transparent);
        private readonly SolidColorBrush lineBrush = new SolidColorBrush(Windows.UI.Colors.Yellow);
        private readonly double lineThickness = 2.0;

        public double CameraAspectRatio { get; set; }
        public int CameraResolutionWidth { get; private set; }
        public int CameraResolutionHeight { get; private set; }

        public int NumFacesOnLastFrame { get; set; }

        public CameraStreamState CameraStreamState { get { return captureManager != null ? captureManager.CameraStreamState : CameraStreamState.NotStreaming; } }

        private MediaCapture captureManager;
        private VideoEncodingProperties videoProperties;
        private FaceTracker faceTracker;
        private ThreadPoolTimer frameProcessingTimer;
        private SemaphoreSlim frameProcessingSemaphore = new SemaphoreSlim(1);
        private AutoCaptureState autoCaptureState;
        private IEnumerable<DetectedFace> detectedFacesFromPreviousFrame;
        private DateTime timeSinceWaitingForStill;
        private DateTime lastTimeWhenAFaceWasDetected;

        private IRealTimeDataProvider realTimeDataProvider;
        private QRCodeProcessor qrCodeProcessor;
        public bool QRCodeMode { get; set; }
        //private IEnumerable<DetectedQRCode> detectedQRCodes = null;
        public DetectedQRCode detctedQRCode { get; set; }

        //private ZXing.Result QRCodeResult; 

        public CameraControl()
        {
            this.InitializeComponent();
        }

        #region Camera stream processing

        public async Task StartStreamAsync(bool isForRealTimeProcessing = false)
        {
            try
            {
                if (captureManager == null ||
                    captureManager.CameraStreamState == CameraStreamState.Shutdown ||
                    captureManager.CameraStreamState == CameraStreamState.NotStreaming)
                {
                    if (captureManager != null)
                    {
                        captureManager.Dispose();
                    }

                    captureManager = new MediaCapture();

                    MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings();
                    var allCameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
                    var selectedCamera = allCameras.FirstOrDefault(c => c.Name == SettingsHelper.Instance.CameraName);
                    if (selectedCamera != null)
                    {
                        settings.VideoDeviceId = selectedCamera.Id;
                    }

                    await captureManager.InitializeAsync(settings);
                    await SetVideoEncodingToHighestResolution(isForRealTimeProcessing);

                    this.webCamCaptureElement.Source = captureManager;
                }

                if (captureManager.CameraStreamState == CameraStreamState.NotStreaming)
                {
                    if (this.faceTracker == null)
                    {
                        this.faceTracker = await FaceTracker.CreateAsync();
                    }

                    this.videoProperties = this.captureManager.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

                    await captureManager.StartPreviewAsync();

                    if (this.frameProcessingTimer != null)
                    {
                        this.frameProcessingTimer.Cancel();
                        frameProcessingSemaphore.Release();
                    }
                    TimeSpan timerInterval = TimeSpan.FromMilliseconds(66); //15fps
                    this.frameProcessingTimer = ThreadPoolTimer.CreatePeriodicTimer(new TimerElapsedHandler(ProcessCurrentVideoFrame), timerInterval);

                    this.cameraControlSymbol.Symbol = Symbol.Camera;
                    this.webCamCaptureElement.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                await Util.GenericApiCallExceptionHandler(ex, "Error starting the camera.");
            }
        }

        private async Task SetVideoEncodingToHighestResolution(bool isForRealTimeProcessing = false)
        {
            VideoEncodingProperties highestVideoEncodingSetting;

            // Sort the available resolutions from highest to lowest
            var availableResolutions = this.captureManager.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview).Cast<VideoEncodingProperties>().OrderByDescending(v => v.Width * v.Height * (v.FrameRate.Numerator / v.FrameRate.Denominator));

            if (isForRealTimeProcessing)
            {
                uint maxHeightForRealTime = 720;
                // Find the highest resolution that is 720p or lower
                highestVideoEncodingSetting = availableResolutions.FirstOrDefault(v => v.Height <= maxHeightForRealTime);
                if (highestVideoEncodingSetting == null)
                {
                    // Since we didn't find 720p or lower, look for the first up from there
                    highestVideoEncodingSetting = availableResolutions.LastOrDefault();
                }
            }
            else
            {
                // Use the highest resolution
                highestVideoEncodingSetting = availableResolutions.FirstOrDefault();
            }

            if (highestVideoEncodingSetting != null)
            {
                this.CameraAspectRatio = (double)highestVideoEncodingSetting.Width / (double)highestVideoEncodingSetting.Height;
                this.CameraResolutionHeight = (int)highestVideoEncodingSetting.Height;
                this.CameraResolutionWidth = (int)highestVideoEncodingSetting.Width;

                await this.captureManager.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, highestVideoEncodingSetting);

                if (this.CameraAspectRatioChanged != null)
                {
                    this.CameraAspectRatioChanged(this, EventArgs.Empty);
                }
            }
        }

        private async void ProcessCurrentVideoFrame(ThreadPoolTimer timer)
        {
            if (captureManager.CameraStreamState != Windows.Media.Devices.CameraStreamState.Streaming
                || !frameProcessingSemaphore.Wait(0))
            {
                return;
            }

            try
            {
                IEnumerable<DetectedFace> faces = null;

                // Create a VideoFrame object specifying the pixel format we want our capture image to be (NV12 bitmap in this case).
                // GetPreviewFrame will convert the native webcam frame into this format.
                const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Nv12;
                using (VideoFrame previewFrame = new VideoFrame(InputPixelFormat, (int)this.videoProperties.Width, (int)this.videoProperties.Height))
                {
                    await this.captureManager.GetPreviewFrameAsync(previewFrame);

                    // The returned VideoFrame should be in the supported NV12 format but we need to verify this.
                    if (FaceDetector.IsBitmapPixelFormatSupported(previewFrame.SoftwareBitmap.BitmapPixelFormat))
                    {
                        faces = await this.faceTracker.ProcessNextFrameAsync(previewFrame);

                        if (this.FilterOutSmallFaces)
                        {
                            // We filter out small faces here. 
                            faces = faces.Where(f => CoreUtil.IsFaceBigEnoughForDetection((int)f.FaceBox.Height, (int)this.videoProperties.Height));
                        }

                        this.NumFacesOnLastFrame = faces.Count();

                        if (this.EnableAutoCaptureMode)
                        {
                            this.UpdateAutoCaptureState(faces);
                        }

                        // Create our visualization using the frame dimensions and face results but run it on the UI thread.
                        var previewFrameSize = new Windows.Foundation.Size(previewFrame.SoftwareBitmap.PixelWidth, previewFrame.SoftwareBitmap.PixelHeight);
                        //this.NumFacesOnLastFrame = 0;
                        if (this.NumFacesOnLastFrame == 0)
                        {
                            System.Diagnostics.Debug.WriteLine("QRCode");
                            this.QRCodeMode = DetectQRCodeInFrame(previewFrame.SoftwareBitmap);
                            if (this.QRCodeMode)
                            {
                                var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                                {
                                    this.ShowQRCodeTrackingVisualization(previewFrameSize, detctedQRCode);

                                });
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("Face");


                            var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                            {
                                this.ShowFaceTrackingVisualization(previewFrameSize, faces);
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                frameProcessingSemaphore.Release();
            }
        }
        private void ShowQRCodeTrackingVisualization(Windows.Foundation.Size framePixelSize, DetectedQRCode QRCode)
        {
            this.FaceTrackingVisualizationCanvas.Children.Clear();

            double actualWidth = this.FaceTrackingVisualizationCanvas.ActualWidth;
            double actualHeight = this.FaceTrackingVisualizationCanvas.ActualHeight;

            if (captureManager.CameraStreamState == Windows.Media.Devices.CameraStreamState.Streaming &&
                QRCode != null && actualWidth != 0 && actualHeight != 0)
            {
                double widthScale = framePixelSize.Width / actualWidth;
                double heightScale = framePixelSize.Height / actualHeight;

                //foreach (DetectedQRCode QRCode in detectedQRCodes)
                //{
                    RealTimeFaceIdentificationBorder faceBorder = new RealTimeFaceIdentificationBorder();
                    this.FaceTrackingVisualizationCanvas.Children.Add(faceBorder);

                    faceBorder.ShowFaceRectangle((uint)(QRCode.QRCodeBox.X / widthScale), (uint)(QRCode.QRCodeBox.Y / heightScale), (uint)(QRCode.QRCodeBox.Width / widthScale), (uint)(QRCode.QRCodeBox.Height / heightScale));
                    
                //}
            }
        }
        private void SetupVisualizationQRCode(WriteableBitmap displaySource, IList<DetectedQRCode> foundFaces)
        {
            ImageBrush brush = new ImageBrush();
            brush.ImageSource = displaySource;
            brush.Stretch = Stretch.Fill;
            this.FaceTrackingVisualizationCanvas.Background = brush;

            if (foundFaces != null)
            {
                double widthScale = displaySource.PixelWidth / this.FaceTrackingVisualizationCanvas.ActualWidth;
                double heightScale = displaySource.PixelHeight / this.FaceTrackingVisualizationCanvas.ActualHeight;

                foreach (DetectedQRCode face in foundFaces)
                {
                    // Create a rectangle element for displaying the face box but since we're using a Canvas
                    // we must scale the rectangles according to the image's actual size.
                    // The original FaceBox values are saved in the Rectangle's Tag field so we can update the
                    // boxes when the Canvas is resized.
                    Windows.UI.Xaml.Shapes.Rectangle box = new Windows.UI.Xaml.Shapes.Rectangle();
                    box.Tag = face.QRCodeBox;
                    box.Width = (uint)(face.QRCodeBox.Width / widthScale);
                    box.Height = (uint)(face.QRCodeBox.Height / heightScale);
                    box.Fill = this.fillBrush;
                    box.Stroke = this.lineBrush;
                    box.StrokeThickness = this.lineThickness;
                    box.Margin = new Thickness((uint)(face.QRCodeBox.X / widthScale), (uint)(face.QRCodeBox.Y / heightScale), 0, 0);

                    this.FaceTrackingVisualizationCanvas.Children.Add(box);
                }
            }

            string message;
            if (foundFaces == null || foundFaces.Count == 0)
            {
                message = "Didn't find any QR Code(s) in the image";
            }
            else if (foundFaces.Count == 1)
            {
                message = "Found a QR Code in the image with Data " + foundFaces[0].QRCodeResult.Text;
            }
            else
            {
                message = "Found " + foundFaces.Count + " QR Codes in the image";
            }

            //this.rootPage.NotifyUser(message, NotifyType.StatusMessage);
        }

        private bool DetectQRCodeInFrame(SoftwareBitmap bitmap)
        {
            SoftwareBitmap convertedSource = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Nv12);
            this.qrCodeProcessor = new QRCodeProcessor();
            this.detctedQRCode = qrCodeProcessor.DecodeQRCodes(convertedSource);

            return this.detctedQRCode != null;
        }

        private void ShowFaceTrackingVisualization(Windows.Foundation.Size framePixelSize, IEnumerable<DetectedFace> detectedFaces)
        {
            this.FaceTrackingVisualizationCanvas.Children.Clear();

            double actualWidth = this.FaceTrackingVisualizationCanvas.ActualWidth;
            double actualHeight = this.FaceTrackingVisualizationCanvas.ActualHeight;

            if (captureManager.CameraStreamState == Windows.Media.Devices.CameraStreamState.Streaming &&
                detectedFaces != null && actualWidth != 0 && actualHeight != 0)
            {
                double widthScale = framePixelSize.Width / actualWidth;
                double heightScale = framePixelSize.Height / actualHeight;

                foreach (DetectedFace face in detectedFaces)
                {
                    RealTimeFaceIdentificationBorder faceBorder = new RealTimeFaceIdentificationBorder();
                    this.FaceTrackingVisualizationCanvas.Children.Add(faceBorder);

                    faceBorder.ShowFaceRectangle((uint)(face.FaceBox.X / widthScale), (uint)(face.FaceBox.Y / heightScale), (uint)(face.FaceBox.Width / widthScale), (uint)(face.FaceBox.Height / heightScale));

                    if (this.realTimeDataProvider != null)
                    {
                        EmotionScores lastEmotion = this.realTimeDataProvider.GetLastEmotionForFace(face.FaceBox);
                        if (lastEmotion != null)
                        {
                            faceBorder.ShowRealTimeEmotionData(lastEmotion);
                        }

                        Face detectedFace = this.realTimeDataProvider.GetLastFaceAttributesForFace(face.FaceBox);
                        IdentifiedPerson identifiedPerson = this.realTimeDataProvider.GetLastIdentifiedPersonForFace(face.FaceBox);
                        SimilarPersistedFace similarPersistedFace = this.realTimeDataProvider.GetLastSimilarPersistedFaceForFace(face.FaceBox);

                        string uniqueId = null;
                        if (similarPersistedFace != null)
                        {
                            uniqueId = similarPersistedFace.PersistedFaceId.ToString("N").Substring(0, 4);
                        }

                        if (detectedFace != null && detectedFace.FaceAttributes != null)
                        {
                            if (identifiedPerson != null && identifiedPerson.Person != null)
                            {
                                // age, gender and id available
                                faceBorder.ShowIdentificationData(detectedFace.FaceAttributes.Age, detectedFace.FaceAttributes.Gender, (uint)Math.Round(identifiedPerson.Confidence * 100), identifiedPerson.Person.Name, uniqueId: uniqueId);
                            }
                            else
                            {
                                // only age and gender available
                                faceBorder.ShowIdentificationData(detectedFace.FaceAttributes.Age, detectedFace.FaceAttributes.Gender, 0, null, uniqueId: uniqueId);
                            }
                        }
                        else if (identifiedPerson != null && identifiedPerson.Person != null)
                        {
                            // only id available
                            faceBorder.ShowIdentificationData(0, null, (uint)Math.Round(identifiedPerson.Confidence * 100), identifiedPerson.Person.Name, uniqueId: uniqueId);
                        }
                        else if (uniqueId != null)
                        {
                            // only unique id available
                            faceBorder.ShowIdentificationData(0, null, 0, null, uniqueId: uniqueId);
                        }
                    }

                    if (SettingsHelper.Instance.ShowDebugInfo)
                    {
                        this.FaceTrackingVisualizationCanvas.Children.Add(new TextBlock
                        {
                            Text = string.Format("Coverage: {0:0}%", 100 * ((double)face.FaceBox.Height / this.videoProperties.Height)),
                            Margin = new Thickness((uint)(face.FaceBox.X / widthScale), (uint)(face.FaceBox.Y / heightScale), 0, 0)
                        });
                    }
                }
            }
        }

        private async void UpdateAutoCaptureState(IEnumerable<DetectedFace> detectedFaces)
        {
            const int IntervalBeforeCheckingForStill = 500;
            const int IntervalWithoutFacesBeforeRevertingToWaitingForFaces = 3;

            if (!detectedFaces.Any())
            {
                if (this.autoCaptureState == AutoCaptureState.WaitingForStillFaces &&
                    (DateTime.Now - this.lastTimeWhenAFaceWasDetected).TotalSeconds > IntervalWithoutFacesBeforeRevertingToWaitingForFaces)
                {
                    this.autoCaptureState = AutoCaptureState.WaitingForFaces;
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        this.OnAutoCaptureStateChanged(this.autoCaptureState);
                    });
                }

                return;
            }

            this.lastTimeWhenAFaceWasDetected = DateTime.Now;

            switch (this.autoCaptureState)
            {
                case AutoCaptureState.WaitingForFaces:
                    // We were waiting for faces and got some... go to the "waiting for still" state
                    this.detectedFacesFromPreviousFrame = detectedFaces;
                    this.timeSinceWaitingForStill = DateTime.Now;
                    this.autoCaptureState = AutoCaptureState.WaitingForStillFaces;

                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        this.OnAutoCaptureStateChanged(this.autoCaptureState);
                    });

                    break;

                case AutoCaptureState.WaitingForStillFaces:
                    // See if we have been waiting for still faces long enough
                    if ((DateTime.Now - this.timeSinceWaitingForStill).TotalMilliseconds >= IntervalBeforeCheckingForStill)
                    {
                        // See if the faces are still enough
                        if (this.AreFacesStill(this.detectedFacesFromPreviousFrame, detectedFaces))
                        {
                            this.autoCaptureState = AutoCaptureState.ShowingCountdownForCapture;
                            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                            {
                                this.OnAutoCaptureStateChanged(this.autoCaptureState);
                            });
                        }
                        else
                        {
                            // Faces moved too much, update the baseline and keep waiting
                            this.timeSinceWaitingForStill = DateTime.Now;
                            this.detectedFacesFromPreviousFrame = detectedFaces;
                        }
                    }
                    break;

                case AutoCaptureState.ShowingCountdownForCapture:
                    break;

                case AutoCaptureState.ShowingCapturedPhoto:
                    break;

                default:
                    break;
            }
        }

        public async Task<ImageAnalyzer> TakeAutoCapturePhoto()
        {
            var image = await CaptureFrameAsync();
            this.autoCaptureState = AutoCaptureState.ShowingCapturedPhoto;
            this.OnAutoCaptureStateChanged(this.autoCaptureState);
            return image;
        }

        public void RestartAutoCaptureCycle()
        {
            this.autoCaptureState = AutoCaptureState.WaitingForFaces;
            this.OnAutoCaptureStateChanged(this.autoCaptureState);
        }

        private bool AreFacesStill(IEnumerable<DetectedFace> detectedFacesFromPreviousFrame, IEnumerable<DetectedFace> detectedFacesFromCurrentFrame)
        {
            int horizontalMovementThreshold = (int)(videoProperties.Width * 0.02);
            int verticalMovementThreshold = (int)(videoProperties.Height * 0.02);

            int numStillFaces = 0;
            int totalFacesInPreviousFrame = detectedFacesFromPreviousFrame.Count();

            foreach (DetectedFace faceInPreviousFrame in detectedFacesFromPreviousFrame)
            {
                if (numStillFaces > 0 && numStillFaces >= totalFacesInPreviousFrame / 2)
                {
                    // If half or more of the faces in the previous frame are considered still we can stop. It is still enough.
                    break;
                }

                // If there is a face in the current frame that is located close enough to this one in the previous frame, we 
                // assume it is the same face and count it as a still face. 
                if (detectedFacesFromCurrentFrame.Any(f => Math.Abs((int)faceInPreviousFrame.FaceBox.X - (int)f.FaceBox.X) <= horizontalMovementThreshold &&
                                                           Math.Abs((int)faceInPreviousFrame.FaceBox.Y - (int)f.FaceBox.Y) <= verticalMovementThreshold))
                {
                    numStillFaces++;
                }
            }

            if (numStillFaces > 0 && numStillFaces >= totalFacesInPreviousFrame / 2)
            {
                // If half or more of the faces in the previous frame are considered still we consider the group as still
                return true;
            }

            return false;
        }

        public async Task StopStreamAsync()
        {
            try
            {
                if (this.frameProcessingTimer != null)
                {
                    this.frameProcessingTimer.Cancel();
                }

                if (captureManager != null && captureManager.CameraStreamState != Windows.Media.Devices.CameraStreamState.Shutdown)
                {
                    this.FaceTrackingVisualizationCanvas.Children.Clear();
                    await this.captureManager.StopPreviewAsync();

                    this.FaceTrackingVisualizationCanvas.Children.Clear();
                    this.webCamCaptureElement.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
                //await Util.GenericApiCallExceptionHandler(ex, "Error stopping the camera.");
            }
        }

        public async Task<ImageAnalyzer> CaptureFrameAsync()
        {
            try
            {
                if (!(await this.frameProcessingSemaphore.WaitAsync(250)))
                {
                    return null;
                }

                const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Nv12;
                var videoFrame = new VideoFrame(BitmapPixelFormat.Bgra8, CameraResolutionWidth, CameraResolutionHeight);
                using (var currentFrame = await captureManager.GetPreviewFrameAsync(videoFrame))
                {
                    using (SoftwareBitmap previewFrame = currentFrame.SoftwareBitmap)
                    {
                        ImageAnalyzer imageWithFace = new ImageAnalyzer(await Util.GetPixelBytesFromSoftwareBitmapAsync(previewFrame));

                        imageWithFace.ShowDialogOnFaceApiErrors = this.ShowDialogOnApiErrors;
                        imageWithFace.FilterOutSmallFaces = this.FilterOutSmallFaces;
                        imageWithFace.UpdateDecodedImageSize(this.CameraResolutionHeight, this.CameraResolutionWidth);
                        imageWithFace.detectedQRCode = this.detctedQRCode;

                        return imageWithFace;
                    }
                }
            }
            catch (Exception ex)
            {
                if (this.ShowDialogOnApiErrors)
                {
                    await Util.GenericApiCallExceptionHandler(ex, "Error capturing photo.");
                }
            }
            finally
            {
                this.frameProcessingSemaphore.Release();
            }

            return null;
        }

        private void OnImageCaptured(ImageAnalyzer imageWithFace)
        {
            if (this.ImageCaptured != null)
            {
                this.ImageCaptured(this, imageWithFace);
            }
        }

        private void OnAutoCaptureStateChanged(AutoCaptureState state)
        {
            if (this.AutoCaptureStateChanged != null)
            {
                this.AutoCaptureStateChanged(this, state);
            }
        }

        #endregion

        public void HideCameraControls()
        {
            this.commandBar.Visibility = Visibility.Collapsed;
        }

        public void SetRealTimeDataProvider(IRealTimeDataProvider provider)
        {
            this.realTimeDataProvider = provider;
        }

        private async void CameraControlButtonClick(object sender, RoutedEventArgs e)
        {
            if (this.cameraControlSymbol.Symbol == Symbol.Camera)
            {
                var img = await CaptureFrameAsync();
                if (img != null)
                {
                    this.cameraControlSymbol.Symbol = Symbol.Refresh;
                    this.OnImageCaptured(img);
                }
            }
            else
            {
                this.cameraControlSymbol.Symbol = Symbol.Camera;

                await StartStreamAsync();

                this.CameraRestarted?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
