namespace Jellyfin.Plugin.FileTransformation.Library
{
    public interface IWebFileTransformationReadService
    {
        bool NeedsTransformation(string path);

        Task RunTransformation(string path, Stream stream);

        /// <summary>
        /// The paths registered as plain file names (not patterns), e.g. main.jellyfin.bundle.js.
        /// </summary>
        IReadOnlyCollection<string> GetRegisteredPaths();
    }
}