//! A resume notification wakes the owner to recheck its boot-clock cutoff.
use super::*;
use std::collections::BTreeMap;
use std::ffi::c_void;
use std::sync::Mutex;
use windows_sys::Win32::System::Power::{
    PowerRegisterSuspendResumeNotification, PowerUnregisterSuspendResumeNotification,
    DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS, HPOWERNOTIFY,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    DEVICE_NOTIFY_CALLBACK, PBT_APMRESUMEAUTOMATIC, PBT_APMRESUMESUSPEND,
};

// Opaque registration cookies let a late callback find no event after teardown.
// Keep the lookup locked through SetEvent so removal joins all event access.
struct Subscriptions {
    events: BTreeMap<usize, usize>,
    next_cookie: usize,
}
static SUBSCRIPTIONS: Mutex<Subscriptions> = Mutex::new(Subscriptions {
    events: BTreeMap::new(),
    next_cookie: 1,
});

unsafe extern "system" fn power_event(context: *const c_void, kind: u32, _: *const c_void) -> u32 {
    if matches!(kind, PBT_APMRESUMEAUTOMATIC | PBT_APMRESUMESUSPEND) {
        let subscriptions = SUBSCRIPTIONS.lock().unwrap_or_else(|p| p.into_inner());
        if let Some(&event) = subscriptions.events.get(&(context as usize)) {
            unsafe {
                SetEvent(event as HANDLE);
            }
        }
    }
    ERROR_SUCCESS
}

pub(super) struct ResumeWake {
    event: OwnedHandle,
    registration: HPOWERNOTIFY,
    cookie: usize,
}

impl ResumeWake {
    pub(super) fn register() -> io::Result<Self> {
        let event = handle(unsafe { CreateEventW(null(), 0, 0, null()) })?;
        let cookie = {
            let mut subscriptions = SUBSCRIPTIONS.lock().unwrap_or_else(|p| p.into_inner());
            let cookie = subscriptions.next_cookie;
            subscriptions.next_cookie = cookie
                .checked_add(1)
                .ok_or_else(|| io::Error::other("Resume notification cookie exhausted"))?;
            subscriptions
                .events
                .insert(cookie, event.as_raw_handle() as usize);
            cookie
        };
        let mut parameters = DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS {
            Callback: Some(power_event),
            Context: cookie as *mut c_void,
        };
        let mut registration = null_mut();
        let status = unsafe {
            PowerRegisterSuspendResumeNotification(
                DEVICE_NOTIFY_CALLBACK,
                (&mut parameters as *mut DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS).cast(),
                &mut registration,
            )
        };
        if status != ERROR_SUCCESS || registration.is_null() {
            SUBSCRIPTIONS
                .lock()
                .unwrap_or_else(|p| p.into_inner())
                .events
                .remove(&cookie);
            return Err(if status == ERROR_SUCCESS {
                io::Error::other("Missing resume notification registration")
            } else {
                io::Error::from_raw_os_error(status as i32)
            });
        }
        Ok(Self {
            event,
            registration: registration as HPOWERNOTIFY,
            cookie,
        })
    }

    pub(super) fn event(&self) -> HANDLE {
        self.event.as_raw_handle()
    }

    pub(super) fn unregister(&mut self) -> io::Result<()> {
        let registration = std::mem::take(&mut self.registration);
        if registration == 0 {
            return Ok(());
        }
        SUBSCRIPTIONS
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .events
            .remove(&self.cookie);
        let status = unsafe { PowerUnregisterSuspendResumeNotification(registration) };
        if status != ERROR_SUCCESS {
            return Err(io::Error::from_raw_os_error(status as i32));
        }
        Ok(())
    }
}

impl Drop for ResumeWake {
    fn drop(&mut self) {
        let _ = self.unregister();
    }
}
