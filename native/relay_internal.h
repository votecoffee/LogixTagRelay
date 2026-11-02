#ifndef LTR_INTERNAL_H
#define LTR_INTERNAL_H
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <stdint.h>
extern SRWLOCK ltr_lock;
extern uint8_t ltr_produced[480], ltr_consumed[480], ltr_app_image[480], ltr_plc_image[480];
extern uint16_t ltr_produced_size, ltr_consumed_size, ltr_input_instance, ltr_output_instance, ltr_config_instance;
extern volatile LONG ltr_connected, ltr_run, ltr_generation;
#endif
