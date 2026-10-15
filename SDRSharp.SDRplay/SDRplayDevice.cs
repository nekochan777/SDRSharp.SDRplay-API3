using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows.Forms;
using SDRSharp.Radio;

namespace SDRSharp.SDRplay
{
    /// <summary>
    /// SDRplay device wrapper for API 3.x (RSP1 / RSP1A).
    /// Public surface kept compatible with the original plugin.
    /// </summary>
    public sealed class SDRplayDevice : IDisposable
    {
        // Sample-rate table (kHz) – same indices the dialog already uses
        /// <summary>
        /// Canonical sample rates in kHz. Index matches the UI dropdown.
        /// Reported to SDR# as Hz (value * 1000). Sent to API as Hz (value * 1000).
        /// </summary>
        public static readonly float[] SampleRatesKHz =
        {
            200, 300, 400, 500, 600, 750, 800, 1000,
            2000, 3000, 4000, 5000, 6000, 7000, 8000, 8192,
            10000
        };

        private volatile bool _running;
        private bool _apiOpened;
        private bool _deviceSelected;

        private NativeMethods.sdrplay_api_DeviceT _device;
        private IntPtr _devHandle = IntPtr.Zero;
        private IntPtr _deviceParamsPtr = IntPtr.Zero;   // -> DeviceParamsT

        private NativeMethods.sdrplay_api_StreamCallback_t _streamCb;
        private NativeMethods.sdrplay_api_EventCallback_t _eventCb;
        private GCHandle _gcHandle;

        private UnsafeBuffer _iqBuffer;
        private unsafe Complex* _iqPtr;
        private int _iqBufferLength;

        private readonly SamplesAvailableEventArgs _eventArgs = new SamplesAvailableEventArgs();

        // Cached configuration
        private long _frequency = 100000000;
        private double _ppm;
        private int _sampleRateIndex = 8;
        private NativeMethods.sdrplay_api_Bw_MHzT _bandwidth = NativeMethods.sdrplay_api_Bw_MHzT.sdrplay_api_BW_1_536;
        private int _ifTypeIndex;
        private int _gainReduction = 40;
        private int _lnaState; // 0 = max gain
        private int _agcSetpoint = -30;
        private bool _agcEnabled = true;
        private int _lnaGrThresh = 59;
        private byte _hwVer;

        public unsafe SDRplayDevice()
        {
            _gcHandle = GCHandle.Alloc(this);
            _streamCb = StreamCallback;
            _eventCb = EventCallback;
        }

        // ------------------------------------------------------------------
        // Public API (compatible with original plugin)
        // ------------------------------------------------------------------
        public event SamplesAvailableDelegate SamplesAvailable;
        public event EventHandler GainReductionUpdate;
        public event EventHandler SampleRateChanged;

        public void OnGainReductionUpdate()
        {
            GainReductionUpdate?.Invoke(this, EventArgs.Empty);
        }

        public void OnSampleRateChanged()
        {
            SampleRateChanged?.Invoke(this, EventArgs.Empty);
        }

        public unsafe void SDRplay_Initalisation()
        {
            if (_running)
                return;

            try
            {
                OpenApiAndSelectDevice();
                ApplyInitialParameters();
                StartStreaming();
                _running = true;
                Trace.WriteLine("SDRplay: streaming started OK");
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SDRplay: initialisation exception: " + ex);
                MessageBox.Show("SDRplay initialisation failed:\n" + ex.Message, "SDRplay",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Stop();
            }
        }

        public void Stop()
        {
            if (!_running && !_deviceSelected && !_apiOpened)
                return;

            _running = false;

            if (_devHandle != IntPtr.Zero)
            {
                try { NativeMethods.sdrplay_api_Uninit(_devHandle); }
                catch { /* ignore */ }
            }

            if (_deviceSelected)
            {
                try { NativeMethods.sdrplay_api_ReleaseDevice(ref _device); }
                catch { /* ignore */ }
                _deviceSelected = false;
                _devHandle = IntPtr.Zero;
                _deviceParamsPtr = IntPtr.Zero;
            }

            if (_apiOpened)
            {
                try { NativeMethods.sdrplay_api_UnlockDeviceApi(); }
                catch { /* ignore */ }
                try { NativeMethods.sdrplay_api_Close(); }
                catch { /* ignore */ }
                _apiOpened = false;
            }

            FreeIqBuffer();
            Trace.WriteLine("SDRplay: stopped");
        }

        public long Frequency
        {
            get => _frequency;
            set
            {
                _frequency = value;
                if (_running) UpdateFrequency();
            }
        }

        /// <summary>Tuned frequency in MHz with PPM applied.</summary>
        public double NominalFrequency =>
            (_frequency / 1.0e6) * (1.0 + _ppm * 1.0e-6);

        public double FQPPM
        {
            get => _ppm;
            set
            {
                _ppm = value;
                if (_running) UpdatePpm();
            }
        }

        public double SampleRate
        {
            get
            {
                int idx = Math.Max(0, Math.Min(_sampleRateIndex, SampleRatesKHz.Length - 1));
                return SampleRatesKHz[idx];
            }
        }

        public int SampleRateIndex
        {
            get => _sampleRateIndex;
            set
            {
                _sampleRateIndex = value;
                if (_running)
                {
                    UpdateSampleRate();
                    OnSampleRateChanged();
                }
            }
        }

        public NativeMethods.sdrplay_api_Bw_MHzT Bandwidth
        {
            get => _bandwidth;
            set
            {
                _bandwidth = value;
                if (_running) UpdateTunerFormat();
            }
        }

        public int IFType
        {
            get => _ifTypeIndex;
            set
            {
                _ifTypeIndex = value;
                if (_running) UpdateTunerFormat();
            }
        }

        public int GainReduction
        {
            get => _gainReduction;
            set
            {
                _gainReduction = Math.Max(20, Math.Min(59, value));
                if (_running && !_agcEnabled)
                    UpdateGain();
            }
        }

        public int Setpoint
        {
            get => _agcSetpoint;
            set
            {
                _agcSetpoint = value;
                if (_running) UpdateAgc();
            }
        }

        public bool AGCEnabled
        {
            get => _agcEnabled;
            set
            {
                _agcEnabled = value;
                if (_running)
                {
                    UpdateAgc();
                    // When leaving AGC, push current manual IF GR + LNA into the API
                    if (!_agcEnabled)
                        UpdateGain();
                }
            }
        }

        /// <summary>
        /// LNA state: 0 = max RF gain. RSP1: 0..3, RSP1A: 0..9.
        /// </summary>
        public int LNAState
        {
            get => _lnaState;
            set
            {
                int maxLna = MaxLnaState;
                _lnaState = Math.Max(0, Math.Min(maxLna, value));
                _lnaGrThresh = _lnaState; // keep legacy field in sync for settings
                if (_running)
                    UpdateGain();
            }
        }

        /// <summary>Max valid LNA state for the selected device.</summary>
        public int MaxLnaState =>
            (_hwVer == NativeMethods.SDRPLAY_RSP1_ID) ? 3 : 9;

        // Legacy name kept so SDRplayIO compiles until fully switched
        public int LNAGRThresh
        {
            get => LNAState;
            set => LNAState = value;
        }

        public byte HardwareVersion => _hwVer;

        public void Dispose()
        {
            Stop();
            if (_gcHandle.IsAllocated)
                _gcHandle.Free();
            GC.SuppressFinalize(this);
        }

        ~SDRplayDevice()
        {
            Dispose();
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------
        private void OpenApiAndSelectDevice()
        {
            var err = NativeMethods.sdrplay_api_Open();
            if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                throw new Exception("sdrplay_api_Open failed: " + NativeMethods.GetErrorString(err));
            _apiOpened = true;
            Trace.WriteLine("SDRplay: Open OK");

            float ver;
            err = NativeMethods.sdrplay_api_ApiVersion(out ver);
            if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                throw new Exception("sdrplay_api_ApiVersion failed: " + NativeMethods.GetErrorString(err));
            Trace.WriteLine($"SDRplay: API version {ver:F2}");

            err = NativeMethods.sdrplay_api_LockDeviceApi();
            if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                throw new Exception("sdrplay_api_LockDeviceApi failed: " + NativeMethods.GetErrorString(err));
            Trace.WriteLine("SDRplay: LockDeviceApi OK");

            var devices = new NativeMethods.sdrplay_api_DeviceT[NativeMethods.SDRPLAY_MAX_DEVICES];
            uint numDevs;
            err = NativeMethods.sdrplay_api_GetDevices(devices, out numDevs, NativeMethods.SDRPLAY_MAX_DEVICES);
            if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                throw new Exception("sdrplay_api_GetDevices failed: " + NativeMethods.GetErrorString(err));

            Trace.WriteLine($"SDRplay: GetDevices numDevs={numDevs}");
            for (int i = 0; i < (int)numDevs; i++)
            {
                Trace.WriteLine($"  [{i}] SerNo='{devices[i].SerNo}' hwVer={devices[i].hwVer} tuner={devices[i].tuner} valid={devices[i].valid} dev=0x{devices[i].dev.ToInt64():X}");
            }

            if (numDevs == 0)
                throw new Exception("No SDRplay devices found.");

            int selected = -1;
            for (int i = 0; i < (int)numDevs; i++)
            {
                byte hw = devices[i].hwVer;
                if (hw == NativeMethods.SDRPLAY_RSP1_ID || hw == NativeMethods.SDRPLAY_RSP1A_ID)
                {
                    selected = i;
                    break;
                }
            }
            if (selected < 0)
                selected = 0;

            _device = devices[selected];
            _hwVer = _device.hwVer;
            _device.tuner = NativeMethods.sdrplay_api_TunerSelectT.sdrplay_api_Tuner_A;
            _device.rspDuoMode = NativeMethods.sdrplay_api_RspDuoModeT.sdrplay_api_RspDuoMode_Single_Tuner;

            Trace.WriteLine($"SDRplay: selecting index={selected} SerNo='{_device.SerNo}' hwVer={_hwVer}");

            err = NativeMethods.sdrplay_api_SelectDevice(ref _device);
            if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                throw new Exception("sdrplay_api_SelectDevice failed: " + NativeMethods.GetErrorString(err));

            _deviceSelected = true;
            _devHandle = _device.dev;
            Trace.WriteLine($"SDRplay: SelectDevice OK handle=0x{_devHandle.ToInt64():X}");

            err = NativeMethods.sdrplay_api_GetDeviceParams(_devHandle, out _deviceParamsPtr);
            if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success || _deviceParamsPtr == IntPtr.Zero)
                throw new Exception("sdrplay_api_GetDeviceParams failed: " + NativeMethods.GetErrorString(err));

            var top = Marshal.PtrToStructure<NativeMethods.sdrplay_api_DeviceParamsT>(_deviceParamsPtr);
            Trace.WriteLine($"SDRplay: DeviceParams @ 0x{_deviceParamsPtr.ToInt64():X}");
            Trace.WriteLine($"  devParams  = 0x{top.devParams.ToInt64():X}");
            Trace.WriteLine($"  rxChannelA = 0x{top.rxChannelA.ToInt64():X}");
            Trace.WriteLine($"  rxChannelB = 0x{top.rxChannelB.ToInt64():X}");
        }

        private void ApplyInitialParameters()
        {
            var top = Marshal.PtrToStructure<NativeMethods.sdrplay_api_DeviceParamsT>(_deviceParamsPtr);

            // ----- DevParams (ppm + sample rate) -----
            if (top.devParams != IntPtr.Zero)
            {
                var dp = Marshal.PtrToStructure<NativeMethods.sdrplay_api_DevParamsT>(top.devParams);
                Trace.WriteLine($"SDRplay: DevParams before: ppm={dp.ppm} fsHz={dp.fsFreq.fsHz}");

                dp.ppm = _ppm;
                dp.fsFreq.fsHz = SampleRatesKHz[_sampleRateIndex] * 1000.0;
                dp.fsFreq.syncUpdate = 0;
                dp.fsFreq.reCal = 0;

                Marshal.StructureToPtr(dp, top.devParams, false);
                Trace.WriteLine($"SDRplay: DevParams after:  ppm={dp.ppm} fsHz={dp.fsFreq.fsHz}");
            }
            else
            {
                Trace.WriteLine("SDRplay: WARNING devParams is null");
            }

            // ----- RxChannelA (tuner + control) -----
            if (top.rxChannelA != IntPtr.Zero)
            {
                var ch = Marshal.PtrToStructure<NativeMethods.sdrplay_api_RxChannelParamsT>(top.rxChannelA);
                Trace.WriteLine($"SDRplay: RxChannelA before: bw={ch.tunerParams.bwType} if={ch.tunerParams.ifType} rfHz={ch.tunerParams.rfFreq.rfHz} gRdB={ch.tunerParams.gain.gRdB} LNA={ch.tunerParams.gain.LNAstate}");

                ch.tunerParams.bwType = _bandwidth;
                ch.tunerParams.ifType = ResolveIfType();
                ch.tunerParams.loMode = NativeMethods.sdrplay_api_LoModeT.sdrplay_api_LO_Auto;

                ch.tunerParams.gain.gRdB = _gainReduction;
                ch.tunerParams.gain.LNAstate = (byte)_lnaState;
                ch.tunerParams.gain.syncUpdate = 0;
                ch.tunerParams.gain.minGr = NativeMethods.sdrplay_api_MinGainReductionT.sdrplay_api_NORMAL_MIN_GR;

                ch.tunerParams.rfFreq.rfHz = _frequency;  // PPM applied via DevParams.ppm
                ch.tunerParams.rfFreq.syncUpdate = 0;

                ch.tunerParams.dcOffsetTuner.dcCal = 3;
                ch.tunerParams.dcOffsetTuner.speedUp = 0;
                ch.tunerParams.dcOffsetTuner.trackTime = 1;
                ch.tunerParams.dcOffsetTuner.refreshRateTime = 2048;

                ch.ctrlParams.dcOffset.DCenable = 1;
                ch.ctrlParams.dcOffset.IQenable = 1;
                ch.ctrlParams.decimation.enable = 0;
                ch.ctrlParams.agc.enable = _agcEnabled
                    ? NativeMethods.sdrplay_api_AgcControlT.sdrplay_api_AGC_50HZ
                    : NativeMethods.sdrplay_api_AgcControlT.sdrplay_api_AGC_DISABLE;
                ch.ctrlParams.agc.setPoint_dBfs = _agcSetpoint;

                Marshal.StructureToPtr(ch, top.rxChannelA, false);
                Trace.WriteLine($"SDRplay: RxChannelA after:  bw={ch.tunerParams.bwType} if={ch.tunerParams.ifType} rfHz={ch.tunerParams.rfFreq.rfHz} gRdB={ch.tunerParams.gain.gRdB} LNA={ch.tunerParams.gain.LNAstate} agc={ch.ctrlParams.agc.enable} sp={ch.ctrlParams.agc.setPoint_dBfs}");
            }
            else
            {
                Trace.WriteLine("SDRplay: WARNING rxChannelA is null");
            }
        }

        /// <summary>IF type for the current IFType/bandwidth selection (Zero vs Low-IF, 2.048 for 1.536 BW).</summary>
        private NativeMethods.sdrplay_api_If_kHzT ResolveIfType()
        {
            if (_ifTypeIndex == 0)
                return NativeMethods.sdrplay_api_If_kHzT.sdrplay_api_IF_Zero;
            if (_bandwidth == NativeMethods.sdrplay_api_Bw_MHzT.sdrplay_api_BW_1_536)
                return NativeMethods.sdrplay_api_If_kHzT.sdrplay_api_IF_2_048;
            return NativeMethods.sdrplay_api_If_kHzT.sdrplay_api_IF_0_450;
        }

        /// <summary>Live sample-rate change (no device teardown).</summary>
        private void UpdateSampleRate()
        {
            if (_devHandle == IntPtr.Zero || _deviceParamsPtr == IntPtr.Zero)
                return;
            try
            {
                var top = Marshal.PtrToStructure<NativeMethods.sdrplay_api_DeviceParamsT>(_deviceParamsPtr);
                if (top.devParams == IntPtr.Zero) return;

                int idx = Math.Max(0, Math.Min(_sampleRateIndex, SampleRatesKHz.Length - 1));
                var dp = Marshal.PtrToStructure<NativeMethods.sdrplay_api_DevParamsT>(top.devParams);
                dp.fsFreq.fsHz = SampleRatesKHz[idx] * 1000.0;
                dp.fsFreq.syncUpdate = 0;
                dp.fsFreq.reCal = 0;
                Marshal.StructureToPtr(dp, top.devParams, false);

                var err = NativeMethods.sdrplay_api_Update(
                    _devHandle,
                    NativeMethods.sdrplay_api_TunerSelectT.sdrplay_api_Tuner_A,
                    NativeMethods.sdrplay_api_ReasonForUpdateT.sdrplay_api_Update_Dev_Fs,
                    NativeMethods.sdrplay_api_ReasonForUpdateExtension1T.sdrplay_api_Update_Ext1_None);

                if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                    Trace.WriteLine("SDRplay: UpdateSampleRate failed: " + NativeMethods.GetErrorString(err));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SDRplay: UpdateSampleRate exception: " + ex.Message);
            }
        }

        /// <summary>Live bandwidth + IF-type change (no device teardown).</summary>
        private void UpdateTunerFormat()
        {
            if (_devHandle == IntPtr.Zero || _deviceParamsPtr == IntPtr.Zero)
                return;
            try
            {
                var top = Marshal.PtrToStructure<NativeMethods.sdrplay_api_DeviceParamsT>(_deviceParamsPtr);
                if (top.rxChannelA == IntPtr.Zero) return;

                var ch = Marshal.PtrToStructure<NativeMethods.sdrplay_api_RxChannelParamsT>(top.rxChannelA);
                ch.tunerParams.bwType = _bandwidth;
                ch.tunerParams.ifType = ResolveIfType();
                Marshal.StructureToPtr(ch, top.rxChannelA, false);

                var err = NativeMethods.sdrplay_api_Update(
                    _devHandle,
                    NativeMethods.sdrplay_api_TunerSelectT.sdrplay_api_Tuner_A,
                    NativeMethods.sdrplay_api_ReasonForUpdateT.sdrplay_api_Update_Tuner_BwType
                        | NativeMethods.sdrplay_api_ReasonForUpdateT.sdrplay_api_Update_Tuner_IfType,
                    NativeMethods.sdrplay_api_ReasonForUpdateExtension1T.sdrplay_api_Update_Ext1_None);

                if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                    Trace.WriteLine("SDRplay: UpdateTunerFormat failed: " + NativeMethods.GetErrorString(err));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SDRplay: UpdateTunerFormat exception: " + ex.Message);
            }
        }

        private void StartStreaming()
        {
            var cbFns = new NativeMethods.sdrplay_api_CallbackFnsT
            {
                StreamACbFn = _streamCb,
                StreamBCbFn = _streamCb,
                EventCbFn = _eventCb
            };

            Trace.WriteLine($"SDRplay: calling Init handle=0x{_devHandle.ToInt64():X}");
            var err = NativeMethods.sdrplay_api_Init(
                _devHandle,
                ref cbFns,
                GCHandle.ToIntPtr(_gcHandle));

            if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                throw new Exception("sdrplay_api_Init failed: " + NativeMethods.GetErrorString(err) + " (" + (int)err + ")");

            Trace.WriteLine("SDRplay: Init OK");
        }

        // ------------------------------------------------------------------
        // Runtime updates
        // ------------------------------------------------------------------
        private void UpdatePpm()
        {
            if (_devHandle == IntPtr.Zero || _deviceParamsPtr == IntPtr.Zero)
                return;
            try
            {
                var top = Marshal.PtrToStructure<NativeMethods.sdrplay_api_DeviceParamsT>(_deviceParamsPtr);
                if (top.devParams == IntPtr.Zero) return;
                var dp = Marshal.PtrToStructure<NativeMethods.sdrplay_api_DevParamsT>(top.devParams);
                dp.ppm = _ppm;
                Marshal.StructureToPtr(dp, top.devParams, false);
                var err = NativeMethods.sdrplay_api_Update(
                    _devHandle,
                    NativeMethods.sdrplay_api_TunerSelectT.sdrplay_api_Tuner_A,
                    NativeMethods.sdrplay_api_ReasonForUpdateT.sdrplay_api_Update_Dev_Ppm,
                    NativeMethods.sdrplay_api_ReasonForUpdateExtension1T.sdrplay_api_Update_Ext1_None);
                if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                    Trace.WriteLine("SDRplay: UpdatePpm failed: " + NativeMethods.GetErrorString(err));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SDRplay: UpdatePpm exception: " + ex.Message);
            }
        }

        private void UpdateFrequency()
        {
            if (_devHandle == IntPtr.Zero || _deviceParamsPtr == IntPtr.Zero)
                return;

            try
            {
                var top = Marshal.PtrToStructure<NativeMethods.sdrplay_api_DeviceParamsT>(_deviceParamsPtr);
                if (top.rxChannelA == IntPtr.Zero) return;

                var ch = Marshal.PtrToStructure<NativeMethods.sdrplay_api_RxChannelParamsT>(top.rxChannelA);
                ch.tunerParams.rfFreq.rfHz = _frequency;  // PPM applied via DevParams.ppm
                ch.tunerParams.rfFreq.syncUpdate = 0;
                Marshal.StructureToPtr(ch, top.rxChannelA, false);

                var err = NativeMethods.sdrplay_api_Update(
                    _devHandle,
                    NativeMethods.sdrplay_api_TunerSelectT.sdrplay_api_Tuner_A,
                    NativeMethods.sdrplay_api_ReasonForUpdateT.sdrplay_api_Update_Tuner_Frf,
                    NativeMethods.sdrplay_api_ReasonForUpdateExtension1T.sdrplay_api_Update_Ext1_None);

                if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                    Trace.WriteLine("SDRplay: UpdateFrequency failed: " + NativeMethods.GetErrorString(err));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SDRplay: UpdateFrequency exception: " + ex.Message);
            }
        }

        private void UpdateGain()
        {
            if (_devHandle == IntPtr.Zero || _deviceParamsPtr == IntPtr.Zero)
                return;

            try
            {
                var top = Marshal.PtrToStructure<NativeMethods.sdrplay_api_DeviceParamsT>(_deviceParamsPtr);
                if (top.rxChannelA == IntPtr.Zero) return;

                var ch = Marshal.PtrToStructure<NativeMethods.sdrplay_api_RxChannelParamsT>(top.rxChannelA);
                ch.tunerParams.gain.gRdB = _gainReduction;
                ch.tunerParams.gain.LNAstate = (byte)_lnaState;
                ch.tunerParams.gain.syncUpdate = 0;
                Marshal.StructureToPtr(ch, top.rxChannelA, false);

                var err = NativeMethods.sdrplay_api_Update(
                    _devHandle,
                    NativeMethods.sdrplay_api_TunerSelectT.sdrplay_api_Tuner_A,
                    NativeMethods.sdrplay_api_ReasonForUpdateT.sdrplay_api_Update_Tuner_Gr,
                    NativeMethods.sdrplay_api_ReasonForUpdateExtension1T.sdrplay_api_Update_Ext1_None);

                if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                    Trace.WriteLine("SDRplay: UpdateGain failed: " + NativeMethods.GetErrorString(err));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SDRplay: UpdateGain exception: " + ex.Message);
            }
        }

        private void UpdateAgc()
        {
            if (_devHandle == IntPtr.Zero || _deviceParamsPtr == IntPtr.Zero)
                return;

            try
            {
                var top = Marshal.PtrToStructure<NativeMethods.sdrplay_api_DeviceParamsT>(_deviceParamsPtr);
                if (top.rxChannelA == IntPtr.Zero) return;

                var ch = Marshal.PtrToStructure<NativeMethods.sdrplay_api_RxChannelParamsT>(top.rxChannelA);
                ch.ctrlParams.agc.enable = _agcEnabled
                    ? NativeMethods.sdrplay_api_AgcControlT.sdrplay_api_AGC_50HZ
                    : NativeMethods.sdrplay_api_AgcControlT.sdrplay_api_AGC_DISABLE;
                ch.ctrlParams.agc.setPoint_dBfs = _agcSetpoint;
                Marshal.StructureToPtr(ch, top.rxChannelA, false);

                var err = NativeMethods.sdrplay_api_Update(
                    _devHandle,
                    NativeMethods.sdrplay_api_TunerSelectT.sdrplay_api_Tuner_A,
                    NativeMethods.sdrplay_api_ReasonForUpdateT.sdrplay_api_Update_Ctrl_Agc,
                    NativeMethods.sdrplay_api_ReasonForUpdateExtension1T.sdrplay_api_Update_Ext1_None);

                if (err != NativeMethods.sdrplay_api_ErrT.sdrplay_api_Success)
                    Trace.WriteLine("SDRplay: UpdateAgc failed: " + NativeMethods.GetErrorString(err));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SDRplay: UpdateAgc exception: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Callbacks
        // ------------------------------------------------------------------
        private unsafe void StreamCallback(
            short* xi,
            short* xq,
            IntPtr paramsPtr,
            uint numSamples,
            uint reset,
            IntPtr cbContext)
        {
            if (!_running || numSamples == 0)
                return;

            try
            {
                EnsureIqBuffer((int)numSamples);

                const float scale = 1.0f / 32768.0f;
                for (int i = 0; i < (int)numSamples; i++)
                {
                    _iqPtr[i].Real = xi[i] * scale;
                    _iqPtr[i].Imag = xq[i] * scale;
                }

                ComplexSamplesAvailable(_iqPtr, (int)numSamples);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SDRplay: StreamCallback exception: " + ex.Message);
            }
        }

        private void EventCallback(
            NativeMethods.sdrplay_api_EventT eventId,
            NativeMethods.sdrplay_api_TunerSelectT tuner,
            IntPtr paramsPtr,
            IntPtr cbContext)
        {
            if (eventId == NativeMethods.sdrplay_api_EventT.sdrplay_api_GainChange)
            {
                OnGainReductionUpdate();
            }
            else if (eventId == NativeMethods.sdrplay_api_EventT.sdrplay_api_DeviceRemoved)
            {
                Trace.WriteLine("SDRplay: device removed");
                _running = false;
            }
        }

        private unsafe void ComplexSamplesAvailable(Complex* buffer, int length)
        {
            var handler = SamplesAvailable;
            if (handler != null)
            {
                _eventArgs.Buffer = buffer;
                _eventArgs.Length = length;
                handler(this, _eventArgs);
            }
        }

        private unsafe void EnsureIqBuffer(int length)
        {
            if (_iqBuffer != null && _iqBufferLength >= length)
                return;

            FreeIqBuffer();
            _iqBuffer = UnsafeBuffer.Create(length, sizeof(Complex));
            _iqPtr = (Complex*)_iqBuffer;
            _iqBufferLength = length;
        }

        private unsafe void FreeIqBuffer()
        {
            if (_iqBuffer != null)
            {
                _iqBuffer.Dispose();
                _iqBuffer = null;
                _iqPtr = null;
                _iqBufferLength = 0;
            }
        }
    }

    public delegate void SamplesAvailableDelegate(object sender, SamplesAvailableEventArgs e);

    public unsafe sealed class SamplesAvailableEventArgs : EventArgs
    {
        public int Length { get; set; }
        public Complex* Buffer { get; set; }
    }
}
