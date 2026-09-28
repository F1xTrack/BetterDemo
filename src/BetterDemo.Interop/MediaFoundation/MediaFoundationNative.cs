using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace BetterDemo.Interop.MediaFoundation;

[SupportedOSPlatform("windows")]
internal static partial class MediaFoundationNative
{
    internal const uint MfVersion = 0x00020070;
    internal const uint MfStartupFull = 0;
    internal const uint SourceReaderFirstVideoStream = 0xFFFFFFFC;
    internal const uint SourceReaderAllStreams = 0xFFFFFFFE;
    internal const int MfENoMoreTypes = unchecked((int)0xC00D36B9);
    internal const uint SourceReaderFlagError = 0x00000001;
    internal const uint SourceReaderFlagEndOfStream = 0x00000002;

    internal static readonly Guid MfDevSourceAttributeSourceType = new("C60AC5FE-252A-478F-A0EF-BC8FA5F7CAD3");
    internal static readonly Guid MfDevSourceAttributeSourceTypeVideoCaptureGuid = new("8AC3587A-4AE7-42D8-99E0-0A6013EEF90F");
    internal static readonly Guid MfDevSourceAttributeFriendlyName = new("60D0E559-52F8-4FA2-BBCE-ACDB34A8EC01");
    internal static readonly Guid MfDevSourceAttributeSourceTypeVidcapSymbolicLink = new("58F0AAD8-22BF-4F8A-BB3D-D2C4978C6E2F");
    internal static readonly Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    internal static readonly Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    internal static readonly Guid MfMtFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    internal static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MfVideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MfVideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MfSourceReaderEnableAdvancedVideoProcessing = new("0F81DA2C-B537-4672-A8B2-A681B17307A3");
    internal static readonly Guid ImfMediaSourceGuid = typeof(IMFMediaSource).GUID;

    [LibraryImport("mfplat.dll")]
    internal static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFShutdown();

    [LibraryImport("mfplat.dll", EntryPoint = "MFCreateAttributes")]
    private static partial int MFCreateAttributesRaw(out nint attributes, uint initialSize);

    [LibraryImport("mfplat.dll", EntryPoint = "MFCreateMediaType")]
    private static partial int MFCreateMediaTypeRaw(out nint mediaType);

    [LibraryImport("mf.dll", EntryPoint = "MFEnumDeviceSources")]
    private static partial int MFEnumDeviceSourcesRaw(nint attributes, out nint activateArray, out uint count);

    [LibraryImport("mfreadwrite.dll", EntryPoint = "MFCreateSourceReaderFromMediaSource")]
    private static partial int MFCreateSourceReaderFromMediaSourceRaw(
        nint mediaSource,
        nint attributes,
        out nint sourceReader);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryPerformanceCounter(out long value);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryPerformanceFrequency(out long value);

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    internal static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new COMException($"{operation} failed (0x{hresult:X8}).", hresult);
        }
    }

    internal static int MFCreateAttributes(out IMFAttributes attributes, uint initialSize)
    {
        var result = MFCreateAttributesRaw(out var pointer, initialSize);
        attributes = result >= 0 ? FromOwnedPointer<IMFAttributes>(pointer) : null!;
        return result;
    }

    internal static int MFCreateMediaType(out IMFMediaType mediaType)
    {
        var result = MFCreateMediaTypeRaw(out var pointer);
        mediaType = result >= 0 ? FromOwnedPointer<IMFMediaType>(pointer) : null!;
        return result;
    }

    internal static int MFEnumDeviceSources(IMFAttributes attributes, out nint activateArray, out uint count)
    {
        var attributesPointer = Marshal.GetIUnknownForObject(attributes);
        try
        {
            return MFEnumDeviceSourcesRaw(attributesPointer, out activateArray, out count);
        }
        finally
        {
            Marshal.Release(attributesPointer);
        }
    }

    internal static int MFCreateSourceReaderFromMediaSource(
        IMFMediaSource mediaSource,
        IMFAttributes? attributes,
        out IMFSourceReader sourceReader)
    {
        var sourcePointer = Marshal.GetIUnknownForObject(mediaSource);
        var attributesPointer = attributes is null ? 0 : Marshal.GetIUnknownForObject(attributes);
        try
        {
            var result = MFCreateSourceReaderFromMediaSourceRaw(sourcePointer, attributesPointer, out var readerPointer);
            sourceReader = result >= 0 ? FromOwnedPointer<IMFSourceReader>(readerPointer) : null!;
            return result;
        }
        finally
        {
            Marshal.Release(sourcePointer);
            if (attributesPointer != 0)
            {
                Marshal.Release(attributesPointer);
            }
        }
    }

    internal static string GetAllocatedString(IMFAttributes attributes, Guid key)
    {
        ThrowIfFailed(attributes.GetAllocatedString(key, out var pointer, out _), nameof(IMFAttributes.GetAllocatedString));
        try
        {
            return Marshal.PtrToStringUni(pointer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void FinalRelease(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static T FromOwnedPointer<T>(nint pointer)
    {
        try
        {
            return (T)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}

[ComImport]
[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid key, nint value);
    [PreserveSig] int GetItemType(in Guid key, out int type);
    [PreserveSig] int CompareItem(in Guid key, nint value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] int GetDouble(in Guid key, out double value);
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint bufferSize, out uint length);
    [PreserveSig] int GetAllocatedString(in Guid key, out nint value, out uint length);
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, [Out] byte[] buffer, uint bufferSize, out uint size);
    [PreserveSig] int GetAllocatedBlob(in Guid key, out nint buffer, out uint size);
    [PreserveSig] int GetUnknown(in Guid key, in Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int SetItem(in Guid key, nint value);
    [PreserveSig] int DeleteItem(in Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    [PreserveSig] int SetDouble(in Guid key, double value);
    [PreserveSig] int SetGUID(in Guid key, in Guid value);
    [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(in Guid key, byte[] buffer, uint size);
    [PreserveSig] int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, nint value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

[ComImport]
[Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate : IMFAttributes
{
    [PreserveSig] new int GetItem(in Guid key, nint value);
    [PreserveSig] new int GetItemType(in Guid key, out int type);
    [PreserveSig] new int CompareItem(in Guid key, nint value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int GetUINT32(in Guid key, out uint value);
    [PreserveSig] new int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] new int GetDouble(in Guid key, out double value);
    [PreserveSig] new int GetGUID(in Guid key, out Guid value);
    [PreserveSig] new int GetStringLength(in Guid key, out uint length);
    [PreserveSig] new int GetString(in Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint bufferSize, out uint length);
    [PreserveSig] new int GetAllocatedString(in Guid key, out nint value, out uint length);
    [PreserveSig] new int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] new int GetBlob(in Guid key, [Out] byte[] buffer, uint bufferSize, out uint size);
    [PreserveSig] new int GetAllocatedBlob(in Guid key, out nint buffer, out uint size);
    [PreserveSig] new int GetUnknown(in Guid key, in Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] new int SetItem(in Guid key, nint value);
    [PreserveSig] new int DeleteItem(in Guid key);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32(in Guid key, uint value);
    [PreserveSig] new int SetUINT64(in Guid key, ulong value);
    [PreserveSig] new int SetDouble(in Guid key, double value);
    [PreserveSig] new int SetGUID(in Guid key, in Guid value);
    [PreserveSig] new int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] new int SetBlob(in Guid key, byte[] buffer, uint size);
    [PreserveSig] new int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out uint count);
    [PreserveSig] new int GetItemByIndex(uint index, out Guid key, nint value);
    [PreserveSig] new int CopyAllItems(IMFAttributes destination);
    [PreserveSig] int ActivateObject(in Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[ComImport]
[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    [PreserveSig] int GetMajorType(out Guid majorType);
    [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);
    [PreserveSig] int IsEqual(IMFMediaType mediaType, out uint flags);
    [PreserveSig] int GetRepresentation(in Guid representation, out nint value);
    [PreserveSig] int FreeRepresentation(in Guid representation, nint value);
}

[ComImport]
[Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaSource
{
    [PreserveSig] int GetEvent(uint flags, out nint mediaEvent);
    [PreserveSig] int BeginGetEvent(nint callback, nint state);
    [PreserveSig] int EndGetEvent(nint result, out nint mediaEvent);
    [PreserveSig] int QueueEvent(int eventType, in Guid extendedType, int status, nint value);
    [PreserveSig] int GetCharacteristics(out uint characteristics);
    [PreserveSig] int CreatePresentationDescriptor(out nint presentationDescriptor);
    [PreserveSig] int Start(nint presentationDescriptor, in Guid timeFormat, nint startPosition);
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Shutdown();
}

[ComImport]
[Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
    [PreserveSig] int SetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
    [PreserveSig] int GetNativeMediaType(uint streamIndex, uint mediaTypeIndex, out IMFMediaType mediaType);
    [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
    [PreserveSig] int SetCurrentMediaType(uint streamIndex, nint reserved, IMFMediaType mediaType);
    [PreserveSig] int SetCurrentPosition(in Guid timeFormat, nint position);
    [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, out uint actualStreamIndex, out uint streamFlags, out long timestamp, out IMFSample? sample);
    [PreserveSig] int Flush(uint streamIndex);
    [PreserveSig] int GetServiceForStream(uint streamIndex, in Guid service, in Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int GetPresentationAttribute(uint streamIndex, in Guid attribute, nint value);
}

[ComImport]
[Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    [PreserveSig] new int GetItem(in Guid key, nint value);
    [PreserveSig] new int GetItemType(in Guid key, out int type);
    [PreserveSig] new int CompareItem(in Guid key, nint value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int GetUINT32(in Guid key, out uint value);
    [PreserveSig] new int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] new int GetDouble(in Guid key, out double value);
    [PreserveSig] new int GetGUID(in Guid key, out Guid value);
    [PreserveSig] new int GetStringLength(in Guid key, out uint length);
    [PreserveSig] new int GetString(in Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint bufferSize, out uint length);
    [PreserveSig] new int GetAllocatedString(in Guid key, out nint value, out uint length);
    [PreserveSig] new int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] new int GetBlob(in Guid key, [Out] byte[] buffer, uint bufferSize, out uint size);
    [PreserveSig] new int GetAllocatedBlob(in Guid key, out nint buffer, out uint size);
    [PreserveSig] new int GetUnknown(in Guid key, in Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] new int SetItem(in Guid key, nint value);
    [PreserveSig] new int DeleteItem(in Guid key);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32(in Guid key, uint value);
    [PreserveSig] new int SetUINT64(in Guid key, ulong value);
    [PreserveSig] new int SetDouble(in Guid key, double value);
    [PreserveSig] new int SetGUID(in Guid key, in Guid value);
    [PreserveSig] new int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] new int SetBlob(in Guid key, byte[] buffer, uint size);
    [PreserveSig] new int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out uint count);
    [PreserveSig] new int GetItemByIndex(uint index, out Guid key, nint value);
    [PreserveSig] new int CopyAllItems(IMFAttributes destination);
    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(uint index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint length);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport]
[Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out nint buffer, out uint maximumLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint currentLength);
    [PreserveSig] int SetCurrentLength(uint currentLength);
    [PreserveSig] int GetMaxLength(out uint maximumLength);
}
