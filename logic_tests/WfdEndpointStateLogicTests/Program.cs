using System.Reflection;
using System.Runtime.CompilerServices;
using NetRelay.ViewModels;

const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
const BindingFlags StaticAny = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

var viewModelType = typeof(ConnectionViewModel);
var rememberEndpoint = viewModelType.GetMethod("RememberEndpointForTransport", InstancePrivate)
    ?? throw new InvalidOperationException("RememberEndpointForTransport was not found.");
var getWifiDirectHost = viewModelType.GetMethod("GetWifiDirectHost", InstancePrivate)
    ?? throw new InvalidOperationException("GetWifiDirectHost was not found.");
var tryGetWifiDirectHost = viewModelType.GetMethod("TryGetWifiDirectHost", StaticAny)
    ?? throw new InvalidOperationException("TryGetWifiDirectHost was not found.");

ConnectionViewModel CreateIsolatedViewModel()
{
    var instance = (ConnectionViewModel)RuntimeHelpers.GetUninitializedObject(viewModelType);
    SetField(instance, "_usbEndpoint", "127.0.0.1:8988");
    SetField(instance, "_wifiDirectEndpoint", "192.168.49.1:8988");
    SetField(instance, "_targetEndpoint", "127.0.0.1:8988");
    return instance;
}

void SetField(ConnectionViewModel instance, string name, string value)
{
    var field = viewModelType.GetField(name, InstancePrivate)
        ?? throw new InvalidOperationException($"{name} was not found.");
    field.SetValue(instance, value);
}

void Remember(ConnectionViewModel instance, string transport, string endpoint) =>
    rememberEndpoint.Invoke(instance, new object?[] { transport, endpoint });

string GetWfdHost(ConnectionViewModel instance) =>
    (string)(getWifiDirectHost.Invoke(instance, null)
        ?? throw new InvalidOperationException("GetWifiDirectHost returned null."));

(bool Accepted, string Host) ParseWfdHost(string endpoint)
{
    object?[] arguments = { endpoint, null };
    var accepted = (bool)(tryGetWifiDirectHost.Invoke(null, arguments) ?? false);
    return (accepted, arguments[1] as string ?? string.Empty);
}

void Assert(string name, bool condition, string details)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{name}=FAIL: {details}");
    }

    Console.WriteLine($"{name}=PASS: {details}");
}

var testA = CreateIsolatedViewModel();
Remember(testA, "usb", "127.0.0.1:8988");
Assert("TEST_A_USB_THEN_WFD", GetWfdHost(testA) == "192.168.49.1",
    "USB loopback did not become the Wi-Fi Direct host.");

var testB = CreateIsolatedViewModel();
Remember(testB, "wifiDirect", "192.168.49.1:8988");
Remember(testB, "usb", "127.0.0.1:8988");
Assert("TEST_B_WFD_THEN_USB_THEN_WFD", GetWfdHost(testB) == "192.168.49.1",
    "The remembered Wi-Fi Direct host survived a USB endpoint update.");

var testC = ParseWfdHost("127.0.0.1:8988");
Assert("TEST_C_REJECT_LOOPBACK", !testC.Accepted,
    "127.0.0.1 was rejected as a Wi-Fi Direct host.");

var testD = ParseWfdHost("192.168.49.1:8988");
Assert("TEST_D_ACCEPT_P2P", testD.Accepted && testD.Host == "192.168.49.1",
    "192.168.49.1 was accepted as the Wi-Fi Direct host.");

Console.WriteLine("WFD_ENDPOINT_STATE_LOGIC_TESTS=PASS");
