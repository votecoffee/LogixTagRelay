/* Application callbacks for the pinned OpENer stack. See THIRD_PARTY_NOTICES.md. */
#include "relay_internal.h"
#include <stdlib.h>
#include <string.h>
#include "opener_api.h"
#include "appcontype.h"

EipStatus ApplicationInitialization(void) {
  if (!CreateAssemblyObject(ltr_input_instance, ltr_produced, ltr_produced_size) ||
      !CreateAssemblyObject(ltr_output_instance, ltr_consumed, ltr_consumed_size) ||
      !CreateAssemblyObject(ltr_config_instance, NULL, 0)) return kEipStatusError;
  ConfigureExclusiveOwnerConnectionPoint(0, ltr_output_instance, ltr_input_instance, ltr_config_instance);
  return kEipStatusOk;
}
void HandleApplication(void) { }
void CheckIoConnectionEvent(unsigned int output_id, unsigned int input_id, IoConnectionEvent event) {
  (void)output_id; (void)input_id;
  AcquireSRWLockExclusive(&ltr_lock);
  memset(ltr_app_image, 0, sizeof(ltr_app_image)); memset(ltr_plc_image, 0, sizeof(ltr_plc_image));
  InterlockedIncrement(&ltr_generation); InterlockedIncrement(&ltr_epoch);
  ReleaseSRWLockExclusive(&ltr_lock);
  InterlockedExchange(&ltr_connected, event == kIoConnectionEventOpened ? 1 : 0);
  if (event != kIoConnectionEventOpened) InterlockedExchange(&ltr_run, 0);
}
EipStatus AfterAssemblyDataReceived(CipInstance *instance) {
  if (instance->instance_number == ltr_output_instance) {
    AcquireSRWLockExclusive(&ltr_lock);
    memcpy(ltr_plc_image, ltr_consumed, ltr_consumed_size);
    InterlockedIncrement(&ltr_generation);
    ReleaseSRWLockExclusive(&ltr_lock);
  }
  return kEipStatusOk;
}
EipBool8 BeforeAssemblyDataSend(CipInstance *instance) {
  if (instance->instance_number == ltr_input_instance) {
    AcquireSRWLockShared(&ltr_lock);
    memcpy(ltr_produced, ltr_app_image, ltr_produced_size);
    ReleaseSRWLockShared(&ltr_lock);
  }
  return true;
}
void RunIdleChanged(EipUint32 run_idle) {
  AcquireSRWLockExclusive(&ltr_lock);
  if ((run_idle & 1u) == 0) {
    memset(ltr_app_image, 0, sizeof(ltr_app_image)); memset(ltr_plc_image, 0, sizeof(ltr_plc_image));
    InterlockedIncrement(&ltr_epoch); InterlockedIncrement(&ltr_generation);
  }
  InterlockedExchange(&ltr_run, (run_idle & 1u) != 0);
  ReleaseSRWLockExclusive(&ltr_lock);
}
EipStatus ResetDevice(void) { CloseAllConnections(); return kEipStatusOk; }
EipStatus ResetDeviceToInitialConfiguration(void) { return ResetDevice(); }
void *CipCalloc(size_t count, size_t size) { return calloc(count, size); }
void CipFree(void *memory) { free(memory); }
