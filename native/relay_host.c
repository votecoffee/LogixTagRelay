/* Small C ABI around OpENer's process-global stack. One start per process. */
#include "relay_internal.h"
#include <string.h>
#include "generic_networkhandler.h"
#include "cipethernetlink.h"
#include "ciptcpipinterface.h"
#include "cipconnectionobject.h"
#include "doublylinkedlist.h"
#include "opener_api.h"

SRWLOCK ltr_lock = SRWLOCK_INIT;
uint8_t ltr_produced[480], ltr_consumed[480], ltr_app_image[480], ltr_plc_image[480];
uint16_t ltr_produced_size, ltr_consumed_size, ltr_input_instance, ltr_output_instance, ltr_config_instance;
volatile LONG ltr_connected, ltr_run, ltr_generation;
static volatile LONG stop_requested, started, failed;
static HANDLE worker;

static DWORD WINAPI ProcessNetwork(LPVOID argument) {
  (void)argument;
  while (!InterlockedCompareExchange(&stop_requested, 0, 0)) {
    if (NetworkHandlerProcessCyclic() != kEipStatusOk) { InterlockedExchange(&failed, 1); break; }
  }
  InterlockedExchange(&ltr_connected, 0);
  NetworkHandlerFinish();
  ShutdownCipStack();
  WSACleanup();
  return 0;
}
__declspec(dllexport) int __cdecl ltr_abi_version(void) { return 1; }
__declspec(dllexport) int __cdecl ltr_start(const char *interface_name, int input_size, int output_size,
  int input_instance, int output_instance, int config_instance, unsigned serial_number, unsigned short connection_seed) {
  uint8_t mac[6];
  if (!interface_name || input_size < 24 || input_size > 480 || output_size < 24 || output_size > 480 ||
      input_instance < 1 || output_instance < 1 || config_instance < 1 || input_instance == output_instance ||
      input_instance == config_instance || output_instance == config_instance ||
      input_instance > 65535 || output_instance > 65535 || config_instance > 65535) return -1;
  if (InterlockedCompareExchange(&started, 1, 0) != 0) return -2;
  // Read the selected NIC; do not change its address, DHCP, or persisted configuration.
  if (IfaceGetMacAddress(interface_name, mac) != kEipStatusOk) return -3;
  ltr_produced_size = (uint16_t)input_size; ltr_consumed_size = (uint16_t)output_size;
  ltr_input_instance = (uint16_t)input_instance; ltr_output_instance = (uint16_t)output_instance; ltr_config_instance = (uint16_t)config_instance;
  DoublyLinkedListInitialize(&connection_list, CipConnectionObjectListArrayAllocator, CipConnectionObjectListArrayFree);
  SetDeviceSerialNumber(serial_number);
  if (CipStackInit(connection_seed) != kEipStatusOk) return -4;
  CipEthernetLinkSetMac(mac);
  if (IfaceGetConfiguration(interface_name, &g_tcpip.interface_configuration) != kEipStatusOk) {
    ShutdownCipStack(); return -5;
  }
  g_network_status.tcp_listener = g_network_status.udp_unicast_listener = g_network_status.udp_global_broadcast_listener = -1;
  g_network_status.udp_io_messaging = -1;
  if (NetworkHandlerInitialize() != kEipStatusOk) { NetworkHandlerFinish(); ShutdownCipStack(); WSACleanup(); return -6; }
  worker = CreateThread(NULL, 0, ProcessNetwork, NULL, 0, NULL);
  if (!worker) { NetworkHandlerFinish(); ShutdownCipStack(); WSACleanup(); return -7; }
  return 0;
}
__declspec(dllexport) int __cdecl ltr_stop(void) {
  if (!worker) return 0;
  InterlockedExchange(&stop_requested, 1);
  // Retain the exact handle if shutdown times out. No forced thread termination or DLL unload.
  if (WaitForSingleObject(worker, 5000) != WAIT_OBJECT_0) return -8;
  CloseHandle(worker); worker = NULL; return 0;
}
__declspec(dllexport) int __cdecl ltr_set_image(const uint8_t *image, int length) {
  if (!image || length != (int)ltr_produced_size || !worker || failed) return -1;
  AcquireSRWLockExclusive(&ltr_lock); memcpy(ltr_app_image, image, length); ReleaseSRWLockExclusive(&ltr_lock);
  return 0;
}
__declspec(dllexport) int __cdecl ltr_get_image(uint8_t *image, int length, int *generation) {
  if (!image || !generation || length != (int)ltr_consumed_size || !worker || failed) return -1;
  AcquireSRWLockShared(&ltr_lock);
  memcpy(image, ltr_plc_image, length); *generation = (int)ltr_generation;
  ReleaseSRWLockShared(&ltr_lock);
  return ltr_connected && ltr_run ? 1 : 0;
}
