using System.Windows;

namespace KronosScreenRemote;

public partial class MainWindow
{
    AudioEngine? _audioEngine;
    TimeSpan     _lastRenderTime = TimeSpan.MinValue;

    void InitAudio()
    {
        VuPickerPopup.Closed += (_, _) => VuPickerBtn.IsChecked = false;

        VuDeviceList.SelectionChanged += (_, _) =>
        {
            if (VuDeviceList.SelectedItem is not AudioDeviceItem item) return;
            VuPickerPopup.IsOpen  = false;
            VuPickerBtn.IsChecked = false;

            _audioEngine?.Stop();
            VuMeter.Reset();
            if (item.Device != null)
            {
                _audioEngine ??= new AudioEngine();
                _settings.VuDeviceId = item.Device.Id;
                if (_connState == ConnState.Connected)
                    _audioEngine.Start(item.Device.Id);
            }
            else
            {
                _settings.VuDeviceId = null;
            }
            Storage.SaveSettings(_settings);
        };

        VuPickerBtn.Checked += (_, _) =>
        {
            PopulateVuDeviceList();
            VuPickerPopup.IsOpen = true;
        };

        if (!string.IsNullOrEmpty(_settings.VuDeviceId))
            _audioEngine = new AudioEngine();
    }

    void StartAudioCapture()
    {
        if (_audioEngine != null && !string.IsNullOrEmpty(_settings.VuDeviceId))
            _audioEngine.Start(_settings.VuDeviceId);
    }

    void StopAudioCapture()
    {
        _audioEngine?.Stop();
        VuMeter.Reset();
    }

    void PopulateVuDeviceList()
    {
        VuDeviceList.Items.Clear();
        VuDeviceList.Items.Add(new AudioDeviceItem(null, "— None (disabled) —"));
        foreach (var dev in AudioEngine.GetDevices())
            VuDeviceList.Items.Add(new AudioDeviceItem(dev, dev.Name));

        foreach (AudioDeviceItem item in VuDeviceList.Items)
        {
            if ((item.Device == null && string.IsNullOrEmpty(_settings.VuDeviceId)) ||
                item.Device?.Id == _settings.VuDeviceId)
            {
                VuDeviceList.SelectedItem = item;
                break;
            }
        }
        if (VuDeviceList.SelectedItem == null && VuDeviceList.Items.Count > 0)
            VuDeviceList.SelectedIndex = 0;
    }

    void CleanupAudio()
    {
        _audioEngine?.Dispose();
        _audioEngine = null;
    }
}

file sealed class AudioDeviceItem(AudioDevice? device, string label)
{
    public AudioDevice? Device { get; } = device;
    public override string ToString() => label;
}
