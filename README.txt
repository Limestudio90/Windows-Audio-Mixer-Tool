
![screen shot](https://i.ibb.co/5h9ZKNtZ/immagine-2025-03-27-182955003.png)


# Windows Audio Mixer - Istruzioni di Installazione

## Requisiti di Sistema
- Windows 10 o Windows 11
- .NET 6.0 SDK o superiore
- Visual Studio 2019/2022 (opzionale, per lo sviluppo)

## Installazione dei Prerequisiti

1. Installare .NET 6.0 SDK:
   - Scaricare il .NET 6.0 SDK da: https://dotnet.microsoft.com/download/dotnet/6.0
   - Eseguire il programma di installazione e seguire le istruzioni a schermo

2. Installare il pacchetto NAudio:
   - Aprire un prompt dei comandi nella cartella del progetto (c:\Users\Utente\Desktop\Mixer\WindowsAudioMixer)
   - Eseguire il comando: dotnet add package NAudio --version 2.1.0

## Compilazione ed Esecuzione

### Tramite Linea di Comando:
1. Aprire un prompt dei comandi nella cartella del progetto
2. Eseguire: dotnet build
3. Eseguire: dotnet run

### Tramite Visual Studio:
1. Aprire il file di soluzione (.sln) con Visual Studio
2. Premere F5 per compilare ed eseguire l'applicazione

## Utilizzo dell'Applicazione

1. Selezionare il dispositivo di output audio dal menu a tendina in alto
2. Utilizzare i cursori per regolare il volume delle singole applicazioni
3. Utilizzare le caselle di controllo "Mute" per disattivare l'audio delle applicazioni
4. Premere il pulsante "Rileva tutte le sessioni" per forzare il rilevamento di tutte le sessioni audio

## Risoluzione dei Problemi

- Se non vengono visualizzate sessioni audio, provare a riprodurre audio in un'applicazione e poi premere il pulsante "Rileva tutte le sessioni"
- Se i browser non vengono rilevati, assicurarsi che stiano riproducendo audio attivamente
- Per problemi di compilazione, verificare che NAudio sia installato correttamente

## Note Aggiuntive

- L'applicazione rileva automaticamente nuove sessioni audio ogni 10 secondi
- I livelli di volume vengono aggiornati ogni 2 secondi
- La funzionalità di reindirizzamento audio tra dispositivi è attualmente in fase di sviluppo
