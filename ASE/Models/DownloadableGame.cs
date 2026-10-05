using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ASE.Models;

public enum DownloadStatus
{
    NotDownloaded,
    Downloading,
    Installed,
    Failed
}

public class DownloadableGame : INotifyPropertyChanged
{
    private DownloadStatus _status = DownloadStatus.NotDownloaded;
    private double _progress = 0;
    private string _statusMessage = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("year")]
    public string Year { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "Game";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("size")]
    public string Size { get; set; } = "";

    [JsonPropertyName("thumbnailUrl")]
    public string ThumbnailUrl { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "Curated";

    [JsonPropertyName("license")]
    public string License { get; set; } = "Freeware / Public Domain";

    [JsonIgnore]
    public string LocalPath { get; set; } = "";

    [JsonIgnore]
    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(IsInstalled));
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(StatusButtonText));
            }
        }
    }

    [JsonIgnore]
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) > 0.001)
            {
                _progress = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public bool IsDownloading => Status == DownloadStatus.Downloading;

    [JsonIgnore]
    public bool IsInstalled => Status == DownloadStatus.Installed;

    [JsonIgnore]
    public bool CanDownload => Status == DownloadStatus.NotDownloaded || Status == DownloadStatus.Failed;

    [JsonIgnore]
    public string StatusButtonText => Status switch
    {
        DownloadStatus.Installed => "Installed",
        DownloadStatus.Downloading => "Downloading...",
        DownloadStatus.Failed => "Retry",
        _ => "Download"
    };

    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
