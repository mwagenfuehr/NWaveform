﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using NAudio.Wave;
using NWaveform.Exceptions;
using NWaveform.Interfaces;
using NWaveform.Model;

namespace NWaveform.NAudio
{
    public class NAudioPlayer : IMediaPlayer, IDisposable
    {
        private readonly IWaveProviderFactory _factory;
        public double MaxRate => 4;
        public double MinRate => 0.25;
        public double RateDelta => 0.25;

        private const double DefaultVolume = 0.5;
        private Uri _source;
        private readonly IWavePlayer _player;
        private IWaveProviderEx _waveProvider;
        private readonly DispatcherTimer _positionTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle);
        private bool _isPlaying;
        private bool _isPaused;
        private bool _isStopped;
        private double _restoreVolume = DefaultVolume;

        public NAudioPlayer(IWavePlayer wavePlayer = null, IWaveProviderFactory factory = null)
        {
            // as a default player DirectSoundOut seems to be the least problematic,
            //  - WaveOut may stutter if doing parallel work on the UI thread since it is UI-synchronuous
            //  - WaveOutEvent seems to have problems on Dispose (NullReference on CloseWaveOut)
            // see also: http://mark-dot-net.blogspot.de/2011/05/naudio-audio-output-devices.html
            _player = wavePlayer ?? new DirectSoundOut(200);
            _player.PlaybackStopped += OnStopped;
            _positionTimer.Interval = TimeSpan.FromMilliseconds(250);
            _positionTimer.Tick += PositionTimerTick;

            _factory = factory ?? new WaveProviderFactory();


            Error = AudioError.NoError;
        }

        public IPlayerError Error { get; }

        public void Play()
        {
            _player.Play();
            _positionTimer.Start();

            IsPlaying = true;
            IsPaused = false;
            IsStopped = false;
        }

        public bool CanPlay => Source != null && !IsPlaying;

        public bool IsPlaying
        {
            get { return _isPlaying; }
            private set
            {
                if (_isPlaying == value) return;
                _isPlaying = value;
                OnPropertyChanged();
                NotifyCanStates();
            }
        }

        public void Pause()
        {
            _player.Pause();
            _positionTimer.Stop();

            IsPlaying = false;
            IsPaused = true;
            IsStopped = false;
        }

        public bool CanPause => IsPlaying;

        public bool IsPaused
        {
            get { return _isPaused; }
            private set
            {
                _isPaused = value;
                OnPropertyChanged();
                NotifyCanStates();
            }
        }

        private void NotifyCanStates()
        {
            // ReSharper disable ExplicitCallerInfoArgument
            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanStop));
            // ReSharper restore ExplicitCallerInfoArgument
        }

        public void Stop()
        {
            _player.Stop();
            _positionTimer.Stop();

            IsPlaying = false;
            IsPaused = false;
            IsStopped = true;

            Position = 0.0;
        }

        public bool CanStop => IsPlaying || IsPaused;

        public bool IsStopped
        {
            get { return _isStopped; }
            private set
            {
                _isStopped = value;
                OnPropertyChanged();
                NotifyCanStates();
            }
        }


        public bool CanMute => Volume > 0;
        public void Mute() { _restoreVolume = Volume; Volume = 0.0; }
        public bool IsMuted => Volume < double.Epsilon;
        public bool CanUnMute => Volume < double.Epsilon;
        public void UnMute() { Volume = _restoreVolume; }

        public AudioSelection AudioSelection { get; set; }
        public bool CanLoop => false;
        public bool IsLooping => false;
        public void ToggleLoop() { }

        public Uri Source
        {
            get { return _source; }
            set
            {
                DisposeMedia();

                Open(value);

                _source = value;
                OnPropertyChanged();

                // ReSharper disable ExplicitCallerInfoArgument
                OnPropertyChanged(nameof(CanPlay));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(HasDuration));
                OnPropertyChanged(nameof(Volume));
                OnPropertyChanged(nameof(CanMute));
                OnPropertyChanged(nameof(CanUnMute));
                OnPropertyChanged(nameof(SupportsBalance));
                // ReSharper restore ExplicitCallerInfoArgument
            }
        }

        private void Open(Uri uri)
        {
            try
            {
                if (uri != null)
                {
                    _waveProvider = _factory.Create(uri);
                    _player.Init(_waveProvider);
                    Stop();
                }
                Error.Exception = null;
            }
            catch (Exception ex) { Error.Exception = new AudioException("Could not open audio", ex); }
        }

        public double Position
        {
            get { return _waveProvider?.CurrentTime.TotalSeconds ?? 0.0; }
            set
            {
                if (_waveProvider == null) return;
                if (PositionCloseTo(_waveProvider.CurrentTime.TotalSeconds, value)) return;

                _waveProvider.CurrentTime = TimeSpan.FromSeconds(value);
                OnPropertyChanged();
            }
        }

        public double Duration => _waveProvider?.TotalTime.TotalSeconds ?? 0.0;
        public bool HasDuration => Duration > 0.0;

        public void Faster() { Rate += RateDelta; }
        public void Slower() { Rate -= RateDelta; }

        public bool SupportsRate => false;
         #pragma warning disable
        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        // TODO: implement speed using SoundTouch/Practice#, cf.:
        // - https://code.google.com/p/practicesharp/ 
        // - http://www.surina.net/soundtouch/ 
        public bool CanFaster => SupportsRate && Source != null && Rate < MaxRate;
        public bool CanSlower => SupportsRate && Source != null && Rate > MinRate;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        #pragma warning restore

        public double Rate
        {
            get { return 1.0; }
            set
            {
                // todo: not builtin to NAudio, use SoundTouch like Practice#, cf.: https://soundtouchdotnet.codeplex.com/
            }
        }

        public double Volume
        {
            get { return _waveProvider?.Volume ?? DefaultVolume; }
            set
            {
                if (_waveProvider == null) return;
                _waveProvider.Volume = (float)value;
                OnPropertyChanged();
                // ReSharper disable ExplicitCallerInfoArgument
                OnPropertyChanged(nameof(CanMute));
                OnPropertyChanged(nameof(CanUnMute));
                OnPropertyChanged(nameof(IsMuted));
                // ReSharper restore ExplicitCallerInfoArgument
            }
        }

        public bool SupportsBalance => _waveProvider != null && _waveProvider.SupportsPanning;

        public double Balance
        {
            get { return _waveProvider?.Pan ?? 0.0; }
            set
            {
                if (_waveProvider == null) return;
                _waveProvider.Pan = (float)value;
                OnPropertyChanged();
            }

        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            DisposeMedia();
            SafeDispose(_player);
        }

        private void DisposeMedia()
        {
            if (IsPlaying) _player.Stop();
            SafeDispose(_waveProvider);
        }

        private static void SafeDispose(object obj)
        {
            (obj as IDisposable)?.Dispose();
        }

        private void OnStopped(object sender, StoppedEventArgs e)
        {
            HandleError(e.Exception);
            Stop();
        }

        private void HandleError(Exception exception)
        {
            if (exception != null)
            {
                var ex = new AudioException("Could not open audio", exception);
                Trace.TraceWarning(ex.ToString());
                Error.Exception = ex;
            }
            else
                Error.Exception = null;
        }

        [DebuggerStepThrough]
        private void PositionTimerTick(object sender, EventArgs e)
        {
            if (!IsPlaying) return;

            // HACK: workaround for NAudios PlaybackStopped event coming way too late (>1sec.)
            // also handles cases where Position > Duration (for wrap around buffers)
            if (Duration - Position <= TimeEpsilon)
            {
                Stop();
                return;
            }

            // ReSharper disable once ExplicitCallerInfoArgument
            OnPropertyChanged(nameof(Position));
        }

        private const double TimeEpsilon = 0.25;
        private bool PositionCloseTo(double a, double b)
        {
            return CloseTo(a, b, TimeEpsilon * Rate);
        }

        private static bool CloseTo(double a, double b, double epsilon)
        {
            return Math.Abs(a - b) <= epsilon;
        }
    }
}