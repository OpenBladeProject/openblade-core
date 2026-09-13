namespace OpenBlade.Service;

internal sealed class PipeListenerEstablishmentException(Exception innerException)
    : IOException("The service pipe listener could not be established.", innerException);
