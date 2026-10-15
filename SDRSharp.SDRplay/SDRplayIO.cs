using System;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using SDRSharp.Radio;

namespace SDRSharp.SDRplay
{
   //public class LimeSDRIO : IFrontendController, IIQStreamController, IDisposable, IFloatingConfigDialogProvider, ITunableSource, ISampleRateChangeSource
//old   public class SDRplayIO : IFrontendController, ITunableSource, IIQStreamController, IDisposable, IFloatingConfigDialogProvider, ISampleRateChangeSource
   public class SDRplayIO : IFrontendController, IIQStreamController, IDisposable, IFloatingConfigDialogProvider, ITunableSource, ISampleRateChangeSource
    {
private const string _displayName = "SDRplay RSP";
        private  SDRplayControllerDialog _gui;
        private SDRplayDevice SDRplayDevice;

        // SDRplay Variables
        private long _frequency;
        private int _GainReduction;
        private double _FqOffsetPPM;
        private int _SP;
        private bool _AGCEnabled = true;
        private int _LNAGRThresh;
        private int _LNAGRThreshMin;
        private NativeMethods.sdrplay_api_Bw_MHzT _IFBandwidth;
        private int _IFType;
        private int SampleRateIndex;

        //Event Handlers
        private SDRSharp.Radio.SamplesAvailableDelegate _callback;
        public event EventHandler SampleRateChanged;

        public SDRplayIO()
        {
            RegistryKey ApiPathkey = null;
            string APIPath, UpdatedPaths, ExistingPaths;

            ApiPathkey = Registry.LocalMachine.OpenSubKey("Software\\SDRplay\\Service\\API", false);
            if (ApiPathkey == null)
            {
                MessageBox.Show("Cannot Locate API");
            }
            else
            {
                string installDir = ApiPathkey.GetValue("Install_Dir", null) as string;
                ApiPathkey.Close();
                if (string.IsNullOrEmpty(installDir))
                {
                    MessageBox.Show("Cannot locate SDRplay API Install_Dir");
                }
                else
                {
                    APIPath = Path.Combine(installDir, Environment.Is64BitProcess ? "x64" : "x86");
                    ExistingPaths = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    // Prepend (not append) so our API dir wins, and skip if already present
                    if (ExistingPaths.IndexOf(APIPath, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        UpdatedPaths = APIPath + Path.PathSeparator + ExistingPaths;
                        Environment.SetEnvironmentVariable("PATH", UpdatedPaths);
                    }
                }
            }
            _gui = new SDRplayControllerDialog(this);
            SDRplayDevice = new SDRplayDevice();

            IFType = (int)Utils.GetIntSetting("SDRplay.IFType", 0);
            Bandwidth = (NativeMethods.sdrplay_api_Bw_MHzT)Utils.GetIntSetting("SDRplay.Bandwidth", (int)NativeMethods.sdrplay_api_Bw_MHzT.sdrplay_api_BW_1_536);
            InternalSampleRate = (int)Utils.GetIntSetting("SDRplay.SampleRate", 8);
            GainReduction = Utils.GetIntSetting("SDRplay.GainReduction", 40);
            if (GainReduction < 20) GainReduction = 20;
            if (GainReduction > 59) GainReduction = 40;
            LNAState = Utils.GetIntSetting("SDRplay.LNAState", Utils.GetIntSetting("SDRplay.LNAGRThresh", 0));        
            AGCsetpoint = Utils.GetIntSetting("SDRplay.AGCsetpoint", -30);
            FQPPM = Utils.GetDoubleSetting("SDRplay.PPM", 0);
            int AGCINT= Utils.GetIntSetting("SDRplay.AGCEnabled", 1);
            if (AGCINT == 1)
                AGCEnabled = true;
            if (AGCINT != 1)
                AGCEnabled = false;
            _gui.InitaliseDisplayValues();
            SDRplayDevice.SamplesAvailable += SDRplayDevice_SamplesAvailable;          
            SDRplayDevice.SampleRateChanged += SDRplayDevice_SampleRateChanged;         
            SDRplayDevice.GainReductionUpdate += SDRplayDevice_GainReductionUpdate;
        }

        ~SDRplayIO()
        {
            Dispose();
        }

        public void Dispose()
        {
            Close();
            if (_gui != null)
            {
                _gui.Close();
                _gui.Dispose();
            }
            GC.SuppressFinalize(this);
        }

        public SDRplayDevice Device
        {
            get
            {
                return SDRplayDevice;
            }
        }

        public void Open()
        {

        }

        public void Close()
        {

        }

        public void Start(Radio.SamplesAvailableDelegate callback)
        {
            _callback = callback;
            SDRplayDevice.SDRplay_Initalisation();
        }

        public void Stop()
        {
            SDRplayDevice.Stop();

        }

        public void ShowSettingGUI(IWin32Window parent)
        {
            if (this._gui.IsDisposed)
                return;
            _gui.Show();
            _gui.Activate();
        }

        public void HideSettingGUI()
        {
            if (_gui.IsDisposed)
                return;
            _gui.Hide();
        }

        public double Samplerate
        {
            get
            {
                return (SDRplayDevice.SampleRate * 1e3);
            }
        }

        internal int InternalSampleRate
        {
            get
            {
                return SampleRateIndex;
            }
            set
            {
                SampleRateIndex = value;
                SDRplayDevice.SampleRateIndex = value; // fires OnSampleRateChanged itself when streaming
                Utils.SaveSetting("SDRplay.SampleRate", value);
            }
        }

        public long Frequency
        {
            get
            {
                return _frequency;
            }
            set
            {
                _frequency = value;
                _gui.GRValues(value);
                SDRplayDevice.Frequency = value;
            }
        }

        public bool IsSoundCardBased
        {
            get
            {
                return false;
            }
        }

        public string SoundCardHint
        {
            get
            {
                return string.Empty;
            }
        }

        private void SDRplayDevice_SampleRateChanged(object sender, EventArgs e)
        {
            if (SampleRateChanged != null)
                SampleRateChanged(this, EventArgs.Empty);
        }

        private void SDRplayDevice_GainReductionUpdate(object sender, EventArgs e)
        {
            // Raised on the native SDRplay event thread. Marshal UI work onto the
            // dialog's thread; touching WinForms controls off-thread crashes SDR#.
            _GainReduction = SDRplayDevice.GainReduction;
            if (_gui == null || _gui.IsDisposed || !_gui.IsHandleCreated)
                return;
            _gui.BeginInvoke((Action)(() =>
            {
                if (!_gui.IsDisposed)
                    _gui.GRboxUpdate(SDRplayDevice.GainReduction, Frequency);
            }));
        }

        private unsafe void SDRplayDevice_SamplesAvailable(object sender, SamplesAvailableEventArgs e)
        {
            _callback(this, e.Buffer, e.Length);
        }

        public int GainReduction
        {
            get
            {
                return _GainReduction;
            }
            set
            {
                SDRplayDevice.GainReduction = value;
                _GainReduction = value;
                _gui.GRboxUpdate(_GainReduction, Frequency);
                Trace.WriteLine("Gain Reduction is" + _GainReduction);
                Utils.SaveSetting("SDRplay.GainReduction", value);
            }
        }

        public double FQPPM
        {
            get
            {
                return _FqOffsetPPM;
            }
            set
            {
                _FqOffsetPPM = value;
                SDRplayDevice.FQPPM = value;
                Utils.SaveSetting("SDRplay.PPM", (double)_FqOffsetPPM);
            }
        }

        public double NominalFrequency
        {
            get
            {
                // Frequency is Hz; result is MHz with PPM applied
                return (Frequency / 1.0e6) * (1.0 + FQPPM * 1.0e-6);
            }
        }

        public int AGCsetpoint
        {
            get
            {
                return _SP;
            }
            set
            {
                _SP = value;
                SDRplayDevice.Setpoint = value;
                Utils.SaveSetting("SDRplay.AGCsetpoint", value);
            }
        }

        public bool AGCEnabled
        {
            get
            {
                return _AGCEnabled;
            }
            set
            {
                _AGCEnabled = value;
                SDRplayDevice.AGCEnabled = value;
                if (_AGCEnabled == true)
                {
                    Utils.SaveSetting("SDRplay.AGCEnabled", 1);
                }
                if (_AGCEnabled == false)
                {
                    Utils.SaveSetting("SDRplay.AGCEnabled", 0);
                }
            }
        }

        /// <summary>Kept for dialog compatibility; no longer used for band thresholds.</summary>
        public int LNAGRThreshMin
        {
            get { return 0; }
            set { _LNAGRThreshMin = 0; }
        }

        /// <summary>LNA state 0..MaxLnaState (0 = max RF gain).</summary>
        public int LNAState
        {
            get { return _LNAGRThresh; }
            set
            {
                int maxLna = MaxLnaState;
                _LNAGRThresh = Math.Max(0, Math.Min(maxLna, value));
                SDRplayDevice.LNAState = _LNAGRThresh;
                Utils.SaveSetting("SDRplay.LNAState", _LNAGRThresh);
            }
        }

        public int MaxLnaState
        {
            get { return SDRplayDevice != null ? SDRplayDevice.MaxLnaState : 3; }
        }

        // Legacy alias
        public int LNAGRThresh
        {
            get { return LNAState; }
            set { LNAState = value; }
        }

        public NativeMethods.sdrplay_api_Bw_MHzT Bandwidth
        {
            get
            {
                return _IFBandwidth;
            }
            set
            {
                _IFBandwidth = value;
                SDRplayDevice.Bandwidth = value;
                Utils.SaveSetting("SDRplay.Bandwidth", (int)_IFBandwidth);
            }
        }

        public int IFType
        {
            get
            {
                return _IFType;
            }
            set
            {
                _IFType = value;
                SDRplayDevice.IFType = value;
                Utils.SaveSetting("SDRplay.IFType", (int)_IFType);
            }
        }

        public bool CanTune
        {
            get
            { return true; }
        }

        public long MaximumTunableFrequency
        {

            get { return 2000000000; }
        }

        public long MinimumTunableFrequency
        {
            get { return 100000; }
        }

    }
}
