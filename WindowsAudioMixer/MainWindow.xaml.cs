using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Drawing;
using System.IO;
using MediaColor = System.Windows.Media.Color;
using DrawingColor = System.Drawing.Color;

namespace WindowsAudioMixer
{
    public partial class MainWindow : Window, IMMNotificationClient
    {
        private MMDeviceEnumerator deviceEnumerator;
        private Dictionary<string, MMDevice> audioDevices;
        private Dictionary<string, AudioSessionControl> audioSessions;
        private Dictionary<string, float> discordVolumes = new Dictionary<string, float>(); // Per salvare i volumi di Discord
        
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
                
            string? selectedDeviceName = OutputDevicesComboBox.SelectedItem.ToString();
            if (string.IsNullOrEmpty(selectedDeviceName) || !audioDevices.ContainsKey(selectedDeviceName))
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
            
            // Cerca specificamente applicazioni problematiche come Discord
            bool discordFound = false;
            
            // Aggiungi prima le applicazioni di sistema che vogliamo sempre mostrare
            try
            {
                // Invece di creare un nuovo AudioSessionControl vuoto, gestiamo il volume master in modo diverso
                // Non aggiungiamo una sessione ma un identificatore speciale
                allSessions.Add((null, "Volume Master", 0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Errore nell'aggiunta del volume master: {ex.Message}");
            }
            
            // Continua con l'enumerazione delle sessioni normali
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
            
            // Aggiungi ricerca specifica per Discord e altre app comuni
            var specificApps = new[] { "Discord", "Spotify", "Teams", "Zoom" };
            foreach (var appName in specificApps)
            {
                try
                {
                    // Per Discord, cerca tutti i processi correlati
                    if (appName == "Discord")
                    {
                        var discordProcesses = System.Diagnostics.Process.GetProcessesByName("Discord");
                        var discordPtbProcesses = System.Diagnostics.Process.GetProcessesByName("DiscordPTB");
                        var discordCanaryProcesses = System.Diagnostics.Process.GetProcessesByName("DiscordCanary");
                        
                        var allDiscordProcesses = discordProcesses
                            .Concat(discordPtbProcesses)
                            .Concat(discordCanaryProcesses)
                            .ToArray();
                        
                        Console.WriteLine($"Trovati {allDiscordProcesses.Length} processi Discord");
                        
                        if (allDiscordProcesses.Length > 0)
                        {
                            bool alreadyDetected = false;
                            
                            // Verifica se Discord è già stato rilevato
                            foreach (var process in allDiscordProcesses)
                            {
                                if (allSessions.Any(s => s.ProcessId == process.Id))
                                {
                                    alreadyDetected = true;
                                    break;
                                }
                            }
                            
                            if (!alreadyDetected)
                            {
                                // Cerca in tutti i dispositivi
                                foreach (var device in audioDevices.Values)
                                {
                                    var deviceSessions = device.AudioSessionManager.Sessions;
                                    for (int i = 0; i < deviceSessions.Count; i++)
                                    {
                                        try
                                        {
                                            var session = deviceSessions[i];
                                            uint processId = session.GetProcessID;
                                            
                                            // Verifica se il processo è uno dei processi Discord
                                            if (allDiscordProcesses.Any(p => p.Id == processId))
                                            {
                                                Console.WriteLine($"Trovata sessione specifica per Discord (PID: {processId})");
                                                allSessions.Add((session, "Discord", processId));
                                                discordFound = true;
                                                break;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"Errore nell'analisi della sessione di Discord: {ex.Message}");
                                        }
                                    }
                                    
                                    if (discordFound) break;
                                }
                            }
                        }
                    }
                    else
                    {
                        // Gestione standard per altre app
                        var processes = System.Diagnostics.Process.GetProcessesByName(appName);
                        if (processes.Length > 0)
                        {
                            bool alreadyDetected = allSessions.Any(s => s.ProcessId == processes[0].Id);
                            if (!alreadyDetected)
                            {
                                // Cerca in tutti i dispositivi
                                foreach (var device in audioDevices.Values)
                                {
                                    var deviceSessions = device.AudioSessionManager.Sessions;
                                    for (int i = 0; i < deviceSessions.Count; i++)
                                    {
                                        try
                                        {
                                            var session = deviceSessions[i];
                                            if (session.GetProcessID == processes[0].Id)
                                            {
                                                Console.WriteLine($"Trovata sessione specifica per {appName}");
                                                allSessions.Add((session, appName, (uint)processes[0].Id));
                                                if (appName == "Discord") discordFound = true;
                                                break;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"Errore nell'analisi della sessione di {appName}: {ex.Message}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Errore nella ricerca di {appName}: {ex.Message}");
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
            
            // Icona dell'applicazione (se disponibile)
            try
            {
                System.Windows.Controls.Image appIcon = null;
                
                if (sessionName == "Volume Master")
                {
                    // Usa un'icona predefinita per il volume master
                    appIcon = new System.Windows.Controls.Image
                    {
                        Width = 24,
                        Height = 24,
                        Margin = new Thickness(5, 0, 5, 0),
                        Source = new BitmapImage(new Uri("pack://application:,,,/WindowsAudioMixer;component/Resources/volume.png", UriKind.Absolute))
                    };
                }
                else if (session.GetProcessID > 0)
                {
                    // Prova a ottenere l'icona dal processo
                    try
                    {
                        var process = System.Diagnostics.Process.GetProcessById((int)session.GetProcessID);
                        if (!string.IsNullOrEmpty(process.MainModule.FileName))
                        {
                            using (Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(process.MainModule.FileName))
                            {
                                if (icon != null)
                                {
                                    using (MemoryStream ms = new MemoryStream())
                                    {
                                        icon.ToBitmap().Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                                        ms.Position = 0;
                                        
                                        BitmapImage bitmapImage = new BitmapImage();
                                        bitmapImage.BeginInit();
                                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                        bitmapImage.StreamSource = ms;
                                        bitmapImage.EndInit();
                                        
                                        appIcon = new System.Windows.Controls.Image
                                        {
                                            Width = 24,
                                            Height = 24,
                                            Margin = new Thickness(5, 0, 5, 0),
                                            Source = bitmapImage
                                        };
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Errore nell'estrazione dell'icona: {ex.Message}");
                    }
                }
                
                // Se non è stato possibile ottenere un'icona, usa un'icona generica
                if (appIcon == null)
                {
                    appIcon = new System.Windows.Controls.Image
                    {
                        Width = 24,
                        Height = 24,
                        Margin = new Thickness(5, 0, 5, 0),
                        Source = new BitmapImage(new Uri("pack://application:,,,/WindowsAudioMixer;component/Resources/app.png", UriKind.Absolute))
                    };
                }
                
                panel.Children.Add(appIcon);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Errore nella creazione dell'icona: {ex.Message}");
            }
            
            // App name
            var nameTextBlock = new TextBlock
            {
                Text = sessionName,
                Width = 180,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(nameTextBlock);
            
            // Volume slider
            var volumeSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Width = 200,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = sessionName
            };
            
            // Gestione speciale per il volume master
            if (sessionName == "Volume Master")
            {
                if (OutputDevicesComboBox.SelectedItem != null)
                {
                    string? selectedDeviceName = OutputDevicesComboBox.SelectedItem.ToString();
                    if (!string.IsNullOrEmpty(selectedDeviceName) && audioDevices.ContainsKey(selectedDeviceName))
                    {
                        var device = audioDevices[selectedDeviceName];
                        volumeSlider.Value = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
                        volumeSlider.ValueChanged += MasterVolumeSlider_ValueChanged;
                    }
                }
            }
            else
            {
                volumeSlider.Value = session.SimpleAudioVolume.Volume * 100;
                volumeSlider.ValueChanged += VolumeSlider_ValueChanged;
            }
            
            panel.Children.Add(volumeSlider);
            
            // Mute button
            var muteButton = new CheckBox
            {
                Content = "Mute",
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = sessionName
            };
            
            // Gestione speciale per il volume master
            if (sessionName == "Volume Master")
            {
                if (OutputDevicesComboBox.SelectedItem != null)
                {
                    string? selectedDeviceName = OutputDevicesComboBox.SelectedItem.ToString();
                    if (!string.IsNullOrEmpty(selectedDeviceName) && audioDevices.ContainsKey(selectedDeviceName))
                    {
                        var device = audioDevices[selectedDeviceName];
                        muteButton.IsChecked = device.AudioEndpointVolume.Mute;
                        muteButton.Checked += MasterMuteButton_CheckedChanged;
                        muteButton.Unchecked += MasterMuteButton_CheckedChanged;
                    }
                }
            }
            else
            {
                muteButton.IsChecked = session.SimpleAudioVolume.Mute;
                muteButton.Checked += MuteButton_CheckedChanged;
                muteButton.Unchecked += MuteButton_CheckedChanged;
            }
            
            panel.Children.Add(muteButton);
            
            // Device selection (solo per le app, non per il volume master)
            if (sessionName != "Volume Master")
            {
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
            }
            
            return panel;
        }

        private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var slider = sender as Slider;
            if (slider == null || slider.Tag == null)
                return;
                
            string? tagValue = slider.Tag.ToString();
            if (string.IsNullOrEmpty(tagValue) || tagValue != "Volume Master")
                return;
                
            if (OutputDevicesComboBox.SelectedItem == null)
                return;
                
            string selectedDeviceName = OutputDevicesComboBox.SelectedItem.ToString();
            if (!audioDevices.ContainsKey(selectedDeviceName))
                return;
                
            var device = audioDevices[selectedDeviceName];
            device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(slider.Value / 100.0);
        }

        private void MasterMuteButton_CheckedChanged(object sender, RoutedEventArgs e)
        {
            var checkbox = sender as CheckBox;
            if (checkbox == null || checkbox.Tag == null)
                return;
                
            if (checkbox.Tag.ToString() != "Volume Master")
                return;
                
            if (OutputDevicesComboBox.SelectedItem == null)
                return;
                
            string selectedDeviceName = OutputDevicesComboBox.SelectedItem.ToString();
            if (!audioDevices.ContainsKey(selectedDeviceName))
                return;
                
            var device = audioDevices[selectedDeviceName];
            device.AudioEndpointVolume.Mute = checkbox.IsChecked ?? false;
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
            if (string.IsNullOrEmpty(sessionName) || !audioSessions.ContainsKey(sessionName))
                return;
                
            var session = audioSessions[sessionName];
            
            try
            {
                bool muteState = checkbox.IsChecked ?? false;
                
                // Gestione speciale per Discord
                if (sessionName.Contains("Discord"))
                {
                    Console.WriteLine($"Applicando mute speciale per Discord: {muteState}");
                    
                    // Salva il volume corrente
                    float currentVolume = session.SimpleAudioVolume.Volume;
                    
                    // Applica il mute
                    session.SimpleAudioVolume.Mute = muteState;
                    
                    // Per Discord, imposta anche il volume a 0 quando è in mute
                    if (muteState)
                    {
                        // Salva il volume attuale in una variabile di classe per ripristinarlo dopo
                        if (!discordVolumes.ContainsKey(sessionName))
                        {
                            discordVolumes[sessionName] = currentVolume;
                        }
                        
                        // Imposta il volume a 0
                        session.SimpleAudioVolume.Volume = 0;
                        
                        // Riapplica il mute dopo un breve ritardo
                        System.Threading.Tasks.Task.Delay(50).ContinueWith(t => 
                        {
                            Application.Current.Dispatcher.Invoke(() => 
                            {
                                try 
                                {
                                    session.SimpleAudioVolume.Mute = true;
                                    session.SimpleAudioVolume.Volume = 0;
                                    Console.WriteLine("Mute di Discord riapplicato con volume 0");
                                }
                                catch (Exception ex) 
                                {
                                    Console.WriteLine($"Errore nel riapplicare mute a Discord: {ex.Message}");
                                }
                            });
                        });
                    }
                    else
                    {
                        // Ripristina il volume precedente quando si toglie il mute
                        if (discordVolumes.ContainsKey(sessionName))
                        {
                            session.SimpleAudioVolume.Volume = discordVolumes[sessionName];
                            discordVolumes.Remove(sessionName);
                        }
                        else
                        {
                            // Se non abbiamo salvato il volume, imposta un valore predefinito
                            session.SimpleAudioVolume.Volume = 0.75f;
                        }
                        
                        Console.WriteLine($"Discord unmute con volume ripristinato");
                    }
                }
                else
                {
                    // Comportamento standard per altre app
                    session.SimpleAudioVolume.Mute = muteState;
                    
                    // ... existing code for other apps ...
                }
            }
            catch (COMException comEx)
            {
                Console.WriteLine($"Errore COM durante il mute di {sessionName}: {comEx.Message}");
                
                // Prova un approccio alternativo in caso di errore COM
                try
                {
                    // Ricarica la sessione e riprova
                    LoadAudioSessions();
                    
                    // Cerca di nuovo la sessione dopo il ricaricamento
                    if (audioSessions.ContainsKey(sessionName))
                    {
                        audioSessions[sessionName].SimpleAudioVolume.Mute = checkbox.IsChecked ?? false;
                        Console.WriteLine($"Mute applicato a {sessionName} dopo il ricaricamento");
                    }
                }
                catch (Exception retryEx)
                {
                    Console.WriteLine($"Errore nel secondo tentativo di mute: {retryEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Errore generico durante il mute di {sessionName}: {ex.Message}");
            }
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
                            
                            // Gestione speciale per il volume master
                            if (sessionName == "Volume Master")
                            {
                                if (OutputDevicesComboBox.SelectedItem != null)
                                {
                                    string selectedDeviceName = OutputDevicesComboBox.SelectedItem.ToString();
                                    if (audioDevices.ContainsKey(selectedDeviceName))
                                    {
                                        var device = audioDevices[selectedDeviceName];
                                        slider.Value = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
                                    }
                                }
                            }
                            else if (audioSessions.ContainsKey(sessionName))
                            {
                                var session = audioSessions[sessionName];
                                slider.Value = session.SimpleAudioVolume.Volume * 100;
                            }
                        }
                        else if (child is CheckBox checkbox && checkbox.Tag != null)
                        {
                            string sessionName = checkbox.Tag.ToString();
                            
                            // Gestione speciale per il volume master
                            if (sessionName == "Volume Master")
                            {
                                if (OutputDevicesComboBox.SelectedItem != null)
                                {
                                    string selectedDeviceName = OutputDevicesComboBox.SelectedItem.ToString();
                                    if (audioDevices.ContainsKey(selectedDeviceName))
                                    {
                                        var device = audioDevices[selectedDeviceName];
                                        checkbox.IsChecked = device.AudioEndpointVolume.Mute;
                                    }
                                }
                            }
                            else if (audioSessions.ContainsKey(sessionName))
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
            // Check if we're currently using light theme
            bool isCurrentlyLightTheme = this.Background is SolidColorBrush brush && 
                                         brush.Color.Equals(Colors.White);
            
            // Clear current resources
            Application.Current.Resources.Clear();
            
            // Create a new resource dictionary
            var themeResources = new ResourceDictionary();
            
            if (isCurrentlyLightTheme)
            {
                // Switch to dark theme
                themeResources.Add("BackgroundBrush", new SolidColorBrush(MediaColor.FromRgb(32, 32, 32)));
                themeResources.Add("ForegroundBrush", new SolidColorBrush(Colors.White));
                themeResources.Add("AccentBrush", new SolidColorBrush(MediaColor.FromRgb(0, 120, 215)));
                
                // Update UI elements to use dark theme
                this.Background = new SolidColorBrush(MediaColor.FromRgb(32, 32, 32));
                this.Foreground = new SolidColorBrush(Colors.White);
                
                // Update button text
                if (sender is Button button)
                {
                    button.Content = "Toggle Light Theme";
                }
            }
            else
            {
                // Switch to light theme
                themeResources.Add("BackgroundBrush", new SolidColorBrush(Colors.White));
                themeResources.Add("ForegroundBrush", new SolidColorBrush(Colors.Black));
                themeResources.Add("AccentBrush", new SolidColorBrush(Colors.DodgerBlue));
                
                // Update UI elements to use light theme
                this.Background = new SolidColorBrush(Colors.White);
                this.Foreground = new SolidColorBrush(Colors.Black);
                
                // Update button text
                if (sender is Button button)
                {
                    button.Content = "Toggle Dark Theme";
                }
            }
            
            Application.Current.Resources.MergedDictionaries.Add(themeResources);
            
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