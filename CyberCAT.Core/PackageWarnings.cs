using WolvenKit.Common;
using WolvenKit.Core.Interfaces;

namespace CyberCAT.Core;

internal sealed class PackageWarnings : ILoggerService
{
    public List<string> Messages { get; } = new();
    public LoggerVerbosity LoggerVerbosity => LoggerVerbosity.Quiet;
    public void SetLoggerVerbosity(LoggerVerbosity verbosity) { }
    public void Info(string message) { }
    public void Success(string message) { }
    public void Debug(string message) { }
    public void Warning(string message) => Messages.Add(message);
    public void Error(string message) => Messages.Add(message);
    public void Error(Exception exception) => Messages.Add(exception.Message);
}
