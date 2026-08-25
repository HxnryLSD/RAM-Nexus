namespace RAM;

/// <summary>
/// Serializes RDD installs across the whole process: the RDD page's manual downloads and
/// the background Default-client updater both hold this gate while writing to the install
/// root, so two installs can never race on the same version folder (e.g. a background
/// update landing on top of a manual one).
/// </summary>
public static class RddInstallGate
{
    public static readonly SemaphoreSlim Semaphore = new(1, 1);
}
