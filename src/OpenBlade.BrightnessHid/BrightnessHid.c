#include "BrightnessHid.h"

static const UCHAR OpenBladeReportDescriptor[] =
{
    0x05, 0x0C,       /* Usage Page (Consumer) */
    0x09, 0x01,       /* Usage (Consumer Control) */
    0xA1, 0x01,       /* Collection (Application) */
    0x85, OPENBLADE_INPUT_REPORT_ID,
    0x15, 0x00,       /* Logical Minimum (0) */
    0x26, 0xFF, 0x03, /* Logical Maximum (0x03FF) */
    0x19, 0x00,       /* Usage Minimum (0) */
    0x2A, 0xFF, 0x03, /* Usage Maximum (0x03FF) */
    0x75, 0x10,       /* Report Size (16) */
    0x95, 0x01,       /* Report Count (1) */
    0x81, 0x00,       /* Input (Data, Array, Absolute) */
    0xC0,

    0x06, 0x00, 0xFF, /* Usage Page (Vendor Defined 0xFF00) */
    0x09, 0x01,
    0xA1, 0x01,
    0x85, OPENBLADE_CONTROL_REPORT_ID,
    0x15, 0x00,
    0x26, 0xFF, 0x03,
    0x09, 0x01,
    0x75, 0x10,
    0x95, 0x01,
    0xB1, 0x00,       /* Feature (Data, Array, Absolute) */
    0xC0
};

static const HID_DESCRIPTOR OpenBladeHidDescriptor =
{
    sizeof(HID_DESCRIPTOR),
    0x21,
    0x0100,
    0,
    1,
    {{HID_REPORT_DESCRIPTOR_TYPE, sizeof(OpenBladeReportDescriptor)}}
};

static BOOLEAN
OpenBladeIsSupportedUsage(
    _In_ USHORT Usage
    )
{
    return Usage == 0 ||
        Usage == OPENBLADE_BRIGHTNESS_INCREMENT ||
        Usage == OPENBLADE_BRIGHTNESS_DECREMENT;
}

static NTSTATUS
OpenBladeCopyToRequest(
    _In_ WDFREQUEST Request,
    _In_reads_bytes_(Length) const VOID* Buffer,
    _In_ size_t Length
    )
{
    WDFMEMORY memory;
    size_t available;
    NTSTATUS status = WdfRequestRetrieveOutputMemory(Request, &memory);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    (VOID)WdfMemoryGetBuffer(memory, &available);
    if (available < Length)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    status = WdfMemoryCopyFromBuffer(memory, 0, Buffer, Length);
    if (NT_SUCCESS(status))
    {
        WdfRequestSetInformation(Request, Length);
    }

    return status;
}

static NTSTATUS
OpenBladeCompleteRead(
    _In_ WDFREQUEST Request,
    _In_ USHORT Usage
    )
{
    OPENBLADE_INPUT_REPORT report;
    report.ReportId = OPENBLADE_INPUT_REPORT_ID;
    report.Usage = Usage;
    return OpenBladeCopyToRequest(Request, &report, sizeof(report));
}

/* Called with StateLock held. The newest state is always retained. */
static VOID
OpenBladeDeliverOrQueueReport(
    _In_ POPENBLADE_DEVICE_CONTEXT Context,
    _In_ USHORT Usage
    )
{
    WDFREQUEST request;
    UCHAR tail;
    NTSTATUS status = WdfIoQueueRetrieveNextRequest(Context->PendingReads, &request);
    if (NT_SUCCESS(status))
    {
        status = OpenBladeCompleteRead(request, Usage);
        WdfRequestComplete(request, status);
        return;
    }

    if (Context->PendingCount == OPENBLADE_PENDING_REPORT_CAPACITY)
    {
        Context->PendingHead =
            (UCHAR)((Context->PendingHead + 1) % OPENBLADE_PENDING_REPORT_CAPACITY);
        Context->PendingCount--;
    }

    tail = (UCHAR)((Context->PendingHead + Context->PendingCount) %
        OPENBLADE_PENDING_REPORT_CAPACITY);
    Context->PendingReports[tail] = Usage;
    Context->PendingCount++;
}

static NTSTATUS
OpenBladeReadReport(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request,
    _Out_ BOOLEAN* CompleteNow
    )
{
    POPENBLADE_DEVICE_CONTEXT context = OpenBladeGetDeviceContext(Device);
    NTSTATUS status;
    *CompleteNow = TRUE;

    WdfWaitLockAcquire(context->StateLock, NULL);
    if (context->PendingCount != 0)
    {
        USHORT usage = context->PendingReports[context->PendingHead];
        context->PendingHead =
            (UCHAR)((context->PendingHead + 1) % OPENBLADE_PENDING_REPORT_CAPACITY);
        context->PendingCount--;
        status = OpenBladeCompleteRead(Request, usage);
    }
    else
    {
        status = WdfRequestForwardToIoQueue(Request, context->PendingReads);
        if (NT_SUCCESS(status))
        {
            *CompleteNow = FALSE;
        }
    }
    WdfWaitLockRelease(context->StateLock);
    return status;
}

static NTSTATUS
OpenBladeGetTransferPacketForWrite(
    _In_ WDFREQUEST Request,
    _Out_ HID_XFER_PACKET* Packet
    )
{
    WDFMEMORY inputMemory;
    WDF_REQUEST_PARAMETERS parameters;
    size_t inputLength;
    NTSTATUS status;

    WDF_REQUEST_PARAMETERS_INIT(&parameters);
    WdfRequestGetParameters(Request, &parameters);
    status = WdfRequestRetrieveInputMemory(Request, &inputMemory);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    Packet->reportBuffer = (PUCHAR)WdfMemoryGetBuffer(inputMemory, &inputLength);
    if (inputLength > MAXULONG ||
        parameters.Parameters.DeviceIoControl.OutputBufferLength > 0xFF)
    {
        return STATUS_INVALID_BUFFER_SIZE;
    }

    Packet->reportBufferLen = (ULONG)inputLength;
    Packet->reportId =
        (UCHAR)parameters.Parameters.DeviceIoControl.OutputBufferLength;
    return STATUS_SUCCESS;
}

static NTSTATUS
OpenBladeGetTransferPacketForRead(
    _In_ WDFREQUEST Request,
    _Out_ HID_XFER_PACKET* Packet
    )
{
    WDFMEMORY inputMemory;
    WDFMEMORY outputMemory;
    size_t inputLength;
    size_t outputLength;
    NTSTATUS status = WdfRequestRetrieveInputMemory(Request, &inputMemory);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    PUCHAR reportId = (PUCHAR)WdfMemoryGetBuffer(inputMemory, &inputLength);
    if (inputLength < sizeof(UCHAR))
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    status = WdfRequestRetrieveOutputMemory(Request, &outputMemory);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    Packet->reportBuffer = (PUCHAR)WdfMemoryGetBuffer(outputMemory, &outputLength);
    if (outputLength > MAXULONG)
    {
        return STATUS_INVALID_BUFFER_SIZE;
    }

    Packet->reportBufferLen = (ULONG)outputLength;
    Packet->reportId = *reportId;
    return STATUS_SUCCESS;
}

static NTSTATUS
OpenBladeSetFeature(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    HID_XFER_PACKET packet;
    NTSTATUS status = OpenBladeGetTransferPacketForWrite(Request, &packet);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    if (packet.reportId != OPENBLADE_CONTROL_REPORT_ID ||
        packet.reportBufferLen != sizeof(OPENBLADE_FEATURE_REPORT))
    {
        return STATUS_INVALID_BUFFER_SIZE;
    }

    POPENBLADE_FEATURE_REPORT report =
        (POPENBLADE_FEATURE_REPORT)packet.reportBuffer;
    if (report->ReportId != OPENBLADE_CONTROL_REPORT_ID ||
        !OpenBladeIsSupportedUsage(report->Usage))
    {
        return STATUS_INVALID_PARAMETER;
    }

    POPENBLADE_DEVICE_CONTEXT context = OpenBladeGetDeviceContext(Device);
    WdfWaitLockAcquire(context->StateLock, NULL);
    /*
     * Brightness usages are retrigger controls whose repeat rate Windows owns.
     * A matching nonzero feature report only renews the lost-release watchdog;
     * it must not produce another input report while the key remains held.
     */
    if (report->Usage != context->CurrentUsage)
    {
        context->CurrentUsage = report->Usage;
        OpenBladeDeliverOrQueueReport(context, report->Usage);
    }

    if (report->Usage == 0)
    {
        (VOID)WdfTimerStop(context->ReleaseWatchdog, FALSE);
    }
    else
    {
        (VOID)WdfTimerStart(
            context->ReleaseWatchdog,
            WDF_REL_TIMEOUT_IN_MS(1000));
    }
    WdfWaitLockRelease(context->StateLock);

    WdfRequestSetInformation(Request, sizeof(OPENBLADE_FEATURE_REPORT));
    return STATUS_SUCCESS;
}

static NTSTATUS
OpenBladeGetFeature(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    HID_XFER_PACKET packet;
    NTSTATUS status = OpenBladeGetTransferPacketForRead(Request, &packet);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    if (packet.reportId != OPENBLADE_CONTROL_REPORT_ID ||
        packet.reportBufferLen != sizeof(OPENBLADE_FEATURE_REPORT))
    {
        return STATUS_INVALID_BUFFER_SIZE;
    }

    POPENBLADE_FEATURE_REPORT report =
        (POPENBLADE_FEATURE_REPORT)packet.reportBuffer;
    POPENBLADE_DEVICE_CONTEXT context = OpenBladeGetDeviceContext(Device);
    WdfWaitLockAcquire(context->StateLock, NULL);
    report->ReportId = OPENBLADE_CONTROL_REPORT_ID;
    report->Usage = context->CurrentUsage;
    WdfWaitLockRelease(context->StateLock);
    WdfRequestSetInformation(Request, sizeof(OPENBLADE_FEATURE_REPORT));
    return STATUS_SUCCESS;
}

VOID
OpenBladeEvtReleaseWatchdog(
    _In_ WDFTIMER Timer
    )
{
    WDFDEVICE device = (WDFDEVICE)WdfTimerGetParentObject(Timer);
    POPENBLADE_DEVICE_CONTEXT context = OpenBladeGetDeviceContext(device);
    WdfWaitLockAcquire(context->StateLock, NULL);
    if (context->CurrentUsage != 0)
    {
        context->CurrentUsage = 0;
        OpenBladeDeliverOrQueueReport(context, 0);
    }
    WdfWaitLockRelease(context->StateLock);
}

VOID
OpenBladeEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    NTSTATUS status;
    BOOLEAN completeNow = TRUE;

    switch (IoControlCode)
    {
    case IOCTL_HID_GET_DEVICE_DESCRIPTOR:
        status = OpenBladeCopyToRequest(
            Request,
            &OpenBladeHidDescriptor,
            sizeof(OpenBladeHidDescriptor));
        break;
    case IOCTL_HID_GET_REPORT_DESCRIPTOR:
        status = OpenBladeCopyToRequest(
            Request,
            OpenBladeReportDescriptor,
            sizeof(OpenBladeReportDescriptor));
        break;
    case IOCTL_HID_GET_DEVICE_ATTRIBUTES:
    {
        HID_DEVICE_ATTRIBUTES attributes;
        RtlZeroMemory(&attributes, sizeof(attributes));
        attributes.Size = sizeof(attributes);
        attributes.VersionNumber = 0x0100;
        status = OpenBladeCopyToRequest(Request, &attributes, sizeof(attributes));
        break;
    }
    case IOCTL_HID_READ_REPORT:
        status = OpenBladeReadReport(device, Request, &completeNow);
        break;
    case IOCTL_UMDF_HID_SET_FEATURE:
        status = OpenBladeSetFeature(device, Request);
        break;
    case IOCTL_UMDF_HID_GET_FEATURE:
        status = OpenBladeGetFeature(device, Request);
        break;
    case IOCTL_HID_ACTIVATE_DEVICE:
    case IOCTL_HID_DEACTIVATE_DEVICE:
        status = STATUS_SUCCESS;
        break;
    default:
        status = STATUS_NOT_SUPPORTED;
        break;
    }

    if (completeNow)
    {
        WdfRequestComplete(Request, status);
    }
}

NTSTATUS
OpenBladeEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    UNREFERENCED_PARAMETER(Driver);
    WdfFdoInitSetFilter(DeviceInit);

    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
        &deviceAttributes,
        OPENBLADE_DEVICE_CONTEXT);
    WDFDEVICE device;
    NTSTATUS status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    POPENBLADE_DEVICE_CONTEXT context = OpenBladeGetDeviceContext(device);
    RtlZeroMemory(context, sizeof(*context));

    WDF_OBJECT_ATTRIBUTES lockAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = device;
    status = WdfWaitLockCreate(&lockAttributes, &context->StateLock);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    WDF_IO_QUEUE_CONFIG manualConfig;
    WDF_IO_QUEUE_CONFIG_INIT(&manualConfig, WdfIoQueueDispatchManual);
    status = WdfIoQueueCreate(
        device,
        &manualConfig,
        WDF_NO_OBJECT_ATTRIBUTES,
        &context->PendingReads);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    WDF_TIMER_CONFIG timerConfig;
    WDF_TIMER_CONFIG_INIT(&timerConfig, OpenBladeEvtReleaseWatchdog);
    WDF_OBJECT_ATTRIBUTES timerAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&timerAttributes);
    timerAttributes.ParentObject = device;
    status = WdfTimerCreate(
        &timerConfig,
        &timerAttributes,
        &context->ReleaseWatchdog);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
        &queueConfig,
        WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = OpenBladeEvtIoDeviceControl;
    return WdfIoQueueCreate(
        device,
        &queueConfig,
        WDF_NO_OBJECT_ATTRIBUTES,
        WDF_NO_HANDLE);
}

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, OpenBladeEvtDeviceAdd);
    return WdfDriverCreate(
        DriverObject,
        RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &config,
        WDF_NO_HANDLE);
}
