using System.ComponentModel.DataAnnotations;

namespace FileConverter.Models;

public class ProxyServer
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = string.Empty;
    
    public int Port { get; set; }
    
    [MaxLength(255)]
    public string? Username { get; set; }
    
    [MaxLength(255)]
    public string? Password { get; set; }
    
    public bool IsActive { get; set; } = true;
    
    public bool IsAvailable { get; set; } = true;
    
    public int ActiveClients { get; set; } = 0;
    
    public DateTime LastChecked { get; set; } = DateTime.UtcNow;
    
    public int ErrorCount { get; set; } = 0;
    
    [MaxLength(1000)]
    public string? LastError { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
} 