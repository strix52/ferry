# Ferry

Ferry is a personal, LAN-only shared thread for messages and file handoff between a laptop and phone. This glossary keeps its deliberately small interaction model unambiguous.

## Language

**Pin**:
A message or file marked as important to the shared Ferry thread and therefore visible on every paired device.
_Avoid_: device pin, local favorite

**Pinned collection**:
The shared, bounded set of the five most recently pinned thread items; adding beyond five replaces its oldest Pin.
_Avoid_: archive, folder, saved list

**Draft**:
Unsent message text private to the browser that created it; it restores automatically into an empty composer and is removed after a successful send or an intentional empty composer.
_Avoid_: pending message, shared draft

**Read state**:
The device-local boundary between thread items already viewed at the bottom of the thread and newer items, represented by a divider and local unread count; it starts at the current latest item on first use.
_Avoid_: delivery receipt, global unread status

**Copy**:
An explicit action on a text message that places only that message's body on the current device's clipboard and briefly confirms completion inline.
_Avoid_: forwarding, export

**Upload queue**:
The device-local ordered set of files waiting to be sent, with exactly one file actively uploading at a time; active and waiting uploads can be cancelled or removed without creating a thread item, failed uploads remain for manual retry, and accepted uploads leave the queue for their normal thread item.
_Avoid_: batch job, background sync
