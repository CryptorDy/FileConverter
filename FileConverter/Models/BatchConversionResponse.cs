namespace FileConverter.Models
{
    public class BatchConversionResponse
    {
        public string BatchId { get; set; } = Guid.NewGuid().ToString();
        public List<ConversionJobResponse> Jobs { get; set; } = new List<ConversionJobResponse>();
        public string BatchStatusUrl { get; set; } = string.Empty;
    }
} 