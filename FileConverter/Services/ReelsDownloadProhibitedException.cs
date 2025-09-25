namespace FileConverter.Services
{
    public class ReelsDownloadProhibitedException : Exception
    {
        public ReelsDownloadProhibitedException(string message) : base(message)
        {
        }
    }
}


