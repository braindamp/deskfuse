namespace DeskFuse.AudioBroker;

internal enum BrokerProofOperation : byte { Acquire = 1, Renew = 2, Supersede = 3, Release = 4 }
internal enum BrokerProofScope : byte { Speaker = 1, Display = 2, Input = 3, ApplicationCapture = 4, Microphone = 5, Camera = 6, Files = 7, Clipboard = 8 }
