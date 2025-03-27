using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Drawing;
using System.IO;

namespace WindowsAudioMixer
{
    public partial class MainWindow : Window, IMMNotificationClient
    {
        private MMDeviceEnumerator deviceEnumerator;
        private Dictionary<string, MMDevice> audioDevices;
        private Dictionary<string, AudioSessionControl> audioSessions;

        public MainWindow()
        {
            InitializeComponent();
            
            deviceEnumerator = new MMDeviceEnumerator();
            audioDevices = new Dictionary<string, MMDevice>();
            audioSessions = new Dictionary<string, AudioSessionControl>();
            
            // Register for device notifications
            deviceEnumerator.RegisterEndpointNotificationCallback(this);
            
            LoadAudioDevices();
            LoadAudioSessions();
            
            // Refresh the UI every 2 seconds to update volume levels
            System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (s, e) => 
            {
                UpdateVolumeLevels();
                // Periodicamente aggiorna anche le sessioni per rilevare nuove app
                if (DateTime.Now.Second % 10 == 0) // Ogni 10 secondi
                {
                    LoadAudioSessions();
                }
            };
            timer.Start();
            
            // Aggiungi un pulsante per forzare il rilevamento delle sessioni audio
            var forceDetectButton = new Button
            {
                Content = "Rileva tutte le sessioni",
                Margin = new Thickness(10, 0, 0, 0)
            };
            forceDetectButton.Click += (s, e) => ForceDetectAllSessions();
            
            // Aggiungi il pulsante alla UI
            if (TopControlsPanel != null)
            {
                TopControlsPanel.Children.Add(forceDetectButton);
            }
        }

        private void LoadAudioDevices()
        {
            OutputDevicesComboBox.Items.Clear();
            audioDevices.Clear();
            
            var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                string deviceName = device.FriendlyName;
                OutputDevicesComboBox.Items.Add(deviceName);
                audioDevices[deviceName] = device;
            }
            
            if (OutputDevicesComboBox.Items.Count > 0)
            {
                OutputDevicesComboBox.SelectedIndex = 0;
            }
        }

        private void LoadAudioSessions()
        {
            AudioSessionsPanel.Children.Clear();
            audioSessions.Clear();
            
            if (OutputDevicesComboBox.SelectedItem == null)
                return;
                
            string selectedDeviceName = OutputDevicesComboBox.SelectedItem.ToString();
            if (!audioDevices.ContainsKey(selectedDeviceName))
                return;
                
            MMDevice selectedDevice = audioDevices[selectedDeviceName];
            
            // Usa SessionManager2 se disponibile per ottenere più informazioni
            // Altrimenti usa SessionManager standard
            var sessionManager = selectedDevice.AudioSessionManager;
            
            // Forza l'aggiornamento delle sessioni
            try
            {
                // Questo metodo può aiutare a rilevare nuove sessioni
                typeof(AudioSessionManager).GetMethod("RefreshSessions")?.Invoke(sessionManager, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Errore nel refresh delle sessioni: {ex.Message}");
            }
            
            var sessionEnumerator = sessionManager.Sessions;
            
            Console.WriteLine($"Trovate {sessionEnumerator.Count} sessioni audio per il dispositivo {selectedDeviceName}");
            
            // Raccogli prima tutte le sessioni per poterle filtrare meglio
            var allSessions = new List<(AudioSessionControl Session, string Name, uint ProcessId)>();
            
            for (int i = 0; i < sessionEnumerator.Count; i++)
            {
                var session = sessionEnumerator[i];
                
                try
                {
                    string sessionIdentifier = session.GetSessionIdentifier;
                    uint processId = session.GetProcessID;
                    
                    Console.WriteLine($"Sessione {i}: ID={sessionIdentifier}, PID={processId}, State={session.State}");
                    
                    // Includi tutte le sessioni tranne quelle di sistema
                    if (sessionIdentifier != null && 
                        (sessionIdentifier.Contains("AudioSrv") || 
                         sessionIdentifier.Contains("System Sounds") ||
                         sessionIdentifier.Contains("WindowsAudioMixer")))
                        continue;
                    
                    string sessionName = GetSessionName(session);
                    if (string.IsNullOrEmpty(sessionName))
                        sessionName = "Unknown Application";
                    
                    allSessions.Add((session, sessionName, processId));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing session: {ex.Message}");
                }
            }
            
            // Aggiungi anche i processi browser noti che potrebbero non essere rilevati automaticamente
            AddKnownBrowserProcesses(allSessions);
            
            // Ora crea l'UI per le sessioni trovate
            foreach (var sessionInfo in allSessions)
            {
                try
                {
                    audioSessions[sessionInfo.Name] = sessionInfo.Session;
                    
                    // Create UI for this session
                    var sessionPanel = CreateSessionUI(sessionInfo.Session, sessionInfo.Name);
                    AudioSessionsPanel.Children.Add(sessionPanel);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error creating UI for session {sessionInfo.Name}: {ex.Message}");
                }
            }
            
            // Se non ci sono sessioni, mostra un messaggio
            if (audioSessions.Count == 0)
            {
                var noSessionsTextBlock = new TextBlock
                {
                    Text = "Nessuna sessione audio attiva trovata. Prova a riprodurre audio in un'applicazione.",
                    Margin = new Thickness(10),
                    TextWrapping = TextWrapping.Wrap
                };
                AudioSessionsPanel.Children.Add(noSessionsTextBlock);
            }
        }
        
        private void AddKnownBrowserProcesses(List<(AudioSessionControl Session, string Name, uint ProcessId)> sessions)
        {
            try
            {
                // Cerca processi di browser noti che potrebbero non essere rilevati automaticamente
                var browserProcesses = System.Diagnostics.Process.GetProcesses()
                    .Where(p => IsBrowser(p.ProcessName))
                    .ToList();
                
                Console.WriteLine($"Trovati {browserProcesses.Count} processi browser nel sistema");
                
                foreach (var process in browserProcesses)
                {
                    try
                    {
                        // Verifica se questo processo è già stato rilevato
                        bool alreadyDetected = sessions.Any(s => s.ProcessId == process.Id);
                        
                        if (!alreadyDetected)
                        {
                            Console.WriteLine($"Browser non rilevato: {process.ProcessName} (PID: {process.Id})");
                            
                            // Prova a trovare la sessione corrispondente in tutti i dispositivi
                            foreach (var device in audioDevices.Values)
                            {
                                var deviceSessions = device.AudioSessionManager.Sessions;
                                
                                for (int i = 0; i < deviceSessions.Count; i++)
                                {
                                    try
                                    {
                                        var session = deviceSessions[i];
                                        
                                        if (session.GetProcessID == process.Id)
                                        {
                                            string name = $"{process.ProcessName} (Browser)";
                                            if (!string.IsNullOrEmpty(process.MainWindowTitle))
                                            {
                                                name = $"{process.ProcessName} - {process.MainWindowTitle}";
                                            }
                                            
                                            Console.WriteLine($"Trovata sessione per browser: {name}");
                                            sessions.Add((session, name, (uint)process.Id));
                                            break;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Errore nell'analisi della sessione del browser: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Errore nell'analisi del processo browser: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Errore nel rilevamento dei browser: {ex.Message}");
            }
        }

        private string GetSessionName(AudioSessionControl session)
        {
            try
            {
                // Prima prova a ottenere il nome visualizzato
                if (!string.IsNullOrEmpty(session.DisplayName))
                {
                    Console.WriteLine($"DisplayName: {session.DisplayName}");
                    return session.DisplayName;
                }
                
                // Poi prova a ottenere il nome del processo
                var processId = session.GetProcessID;
                Console.WriteLine($"ProcessID: {processId}");
                
                if (processId > 0)
                {
                    try
                    {
                        var process = System.Diagnostics.Process.GetProcessById((int)processId);
                        string processName = process.ProcessName;
                        
                        Console.WriteLine($"ProcessName: {processName}, MainWindowTitle: {process.MainWindowTitle}");
                        
                        // Per i browser, aggiungi informazioni aggiuntive e assicurati che vengano rilevati
                        if (IsBrowser(processName))
                        {
                            // Per Chrome, cerca di ottenere informazioni più specifiche
                            if (processName.ToLower().Contains("chrome") || 
                                processName.ToLower().Contains("edge") ||
                                processName.ToLower().Contains("firefox"))
                            {
                                // Cerca di ottenere il titolo della scheda
                                if (!string.IsNullOrEmpty(process.MainWindowTitle))
                                {
                                    return $"{processName} - {process.MainWindowTitle}";
                                }
                            }
                            
                            return $"{processName} (Browser)";
                        }
                        
                        // Prova a ottenere il titolo della finestra principale
                        if (!string.IsNullOrEmpty(process.MainWindowTitle))
                        {
                            return $"{processName} - {process.MainWindowTitle}";
                        }
                        
                        return processName;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error getting process info: {ex.Message}");
                        return $"Process ID: {processId}";
                    }
                }
                
                // Se tutto fallisce, usa l'identificatore di sessione
                string sessionId = session.GetSessionIdentifier;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    Console.WriteLine($"SessionID: {sessionId}");
                    
                    // Cerca di estrarre informazioni utili dall'ID di sessione
                    if (sessionId.Contains("\\"))
                    {
                        string[] parts = sessionId.Split('\\');
                        string lastPart = parts[parts.Length - 1];
                        
                        // Se contiene un nome di browser, evidenzialo
                        if (IsBrowser(lastPart))
                        {
                            return $"{lastPart} (Browser)";
                        }
                        
                        return lastPart;
                    }
                    return sessionId;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting session name: {ex.Message}");
            }
            
            return "Unknown Application";
        }
        
        private bool IsBrowser(string processName)
        {
            // Lista dei nomi di processo comuni per i browser
            string[] browserProcesses = new string[] 
            { 
                "chrome", "firefox", "msedge", "iexplore", "opera", 
                "brave", "vivaldi", "safari", "chromium" 
            };
            
            processName = processName.ToLower();
            return browserProcesses.Any(browser => processName.Contains(browser));
        }

        private StackPanel CreateSessionUI(AudioSessionControl session, string sessionName)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 5, 0, 5)
            };
            
            // App name
            var nameTextBlock = new TextBlock
            {
                Text = sessionName,
                Width = 200,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(nameTextBlock);
            
            // Volume slider
            var volumeSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Width = 200,
                Value = session.SimpleAudioVolume.Volume * 100,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = sessionName
            };
            volumeSlider.ValueChanged += VolumeSlider_ValueChanged;
            panel.Children.Add(volumeSlider);
            
            // Mute button
            var muteButton = new CheckBox
            {
                Content = "Mute",
                IsChecked = session.SimpleAudioVolume.Mute,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = sessionName
            };
            muteButton.Checked += MuteButton_CheckedChanged;
            muteButton.Unchecked += MuteButton_CheckedChanged;
            panel.Children.Add(muteButton);
            
            // Device selection
            var deviceComboBox = new ComboBox
            {
                Width = 200,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = sessionName
            };
            
            foreach (var device in audioDevices)
            {
                deviceComboBox.Items.Add(device.Key);
            }
            
            deviceComboBox.SelectedItem = OutputDevicesComboBox.SelectedItem;
            deviceComboBox.SelectionChanged += DeviceComboBox_SelectionChanged;
            panel.Children.Add(deviceComboBox);
            
            return panel;
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var slider = sender as Slider;
            if (slider == null || slider.Tag == null)
                return;
                
            string sessionName = slider.Tag.ToString();
            if (!audioSessions.ContainsKey(sessionName))
                return;
                
            var session = audioSessions[sessionName];
            session.SimpleAudioVolume.Volume = (float)(slider.Value / 100.0);
        }

        private void MuteButton_CheckedChanged(object sender, RoutedEventArgs e)
        {
            var checkbox = sender as CheckBox;
            if (checkbox == null || checkbox.Tag == null)
                return;
                
            string sessionName = checkbox.Tag.ToString();
            if (!audioSessions.ContainsKey(sessionName))
                return;
                
            var session = audioSessions[sessionName];
            session.SimpleAudioVolume.Mute = checkbox.IsChecked ?? false;
        }

        private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var comboBox = sender as ComboBox;
            if (comboBox == null || comboBox.Tag == null || comboBox.SelectedItem == null)
                return;
                
            string sessionName = comboBox.Tag.ToString();
            string targetDeviceName = comboBox.SelectedItem.ToString();
            
            if (!audioSessions.ContainsKey(sessionName) || !audioDevices.ContainsKey(targetDeviceName))
                return;
                
            // Note: Redirecting audio streams to different devices requires more complex API calls
            // This is a placeholder for that functionality
            MessageBox.Show($"Redirecting {sessionName} to {targetDeviceName} - This feature requires additional Windows API calls");
        }

        private void UpdateVolumeLevels()
        {
            foreach (UIElement element in AudioSessionsPanel.Children)
            {
                if (element is StackPanel panel)
                {
                    foreach (UIElement child in panel.Children)
                    {
                        if (child is Slider slider && slider.Tag != null)
                        {
                            string sessionName = slider.Tag.ToString();
                            if (audioSessions.ContainsKey(sessionName))
                            {
                                var session = audioSessions[sessionName];
                                slider.Value = session.SimpleAudioVolume.Volume * 100;
                            }
                        }
                        else if (child is CheckBox checkbox && checkbox.Tag != null)
                        {
                            string sessionName = checkbox.Tag.ToString();
                            if (audioSessions.ContainsKey(sessionName))
                            {
                                var session = audioSessions[sessionName];
                                checkbox.IsChecked = session.SimpleAudioVolume.Mute;
                            }
                        }
                    }
                }
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadAudioSessions();
        }

        private void OutputDevicesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadAudioSessions();
        }

        // IMMNotificationClient implementation for device change notifications
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Dispatcher.Invoke(LoadAudioDevices);
        public void OnDeviceAdded(string pwstrDeviceId) => Dispatcher.Invoke(LoadAudioDevices);
        public void OnDeviceRemoved(string deviceId) => Dispatcher.Invoke(LoadAudioDevices);
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Dispatcher.Invoke(LoadAudioDevices);
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        
        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            // Revert to using the default light theme
            Application.Current.Resources.Clear();
            
            // Apply basic light theme styles
            var lightTheme = new ResourceDictionary();
            lightTheme.Add("BackgroundBrush", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White));
            lightTheme.Add("ForegroundBrush", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black));
            lightTheme.Add("AccentBrush", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DodgerBlue));
            
            Application.Current.Resources.MergedDictionaries.Add(lightTheme);
            
            // Update UI elements to use light theme
            this.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
            this.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black);
            
            // Refresh the UI to apply the theme
            LoadAudioSessions();
        }

        private void ForceDetectAllSessions()
        {
            try
            {
                // Cerca di forzare l'aggiornamento delle sessioni audio
                Console.WriteLine("Forzando il rilevamento di tutte le sessioni audio...");
                
                // Enumera tutti i dispositivi, non solo quelli attivi
                var allDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
                foreach (var device in allDevices)
                {
                    try
                    {
                        Console.WriteLine($"Dispositivo: {device.FriendlyName}, Stato: {device.State}");
                        
                        if (device.State == DeviceState.Active)
                        {
                            var sessionManager = device.AudioSessionManager;
                            var sessions = sessionManager.Sessions;
                            
                            Console.WriteLine($"  Sessioni trovate: {sessions.Count}");
                            
                            for (int i = 0; i < sessions.Count; i++)
                            {
                                try
                                {
                                    var session = sessions[i];
                                    string id = session.GetSessionIdentifier;
                                    uint pid = session.GetProcessID;
                                    
                                    Console.WriteLine($"  Sessione {i}: ID={id}, PID={pid}, State={session.State}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"  Errore nell'analisi della sessione: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Errore nell'analisi del dispositivo: {ex.Message}");
                    }
                    finally
                    {
                        // Rilascia le risorse
                        if (device != null)
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                }
                
                // Ricarica le sessioni audio
                LoadAudioSessions();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Errore nel rilevamento forzato: {ex.Message}");
            }
        }
    }
}    