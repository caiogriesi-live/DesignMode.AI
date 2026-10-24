// Native dialog provides focus containment, Escape handling, and focus restoration.
export function setOpen(dialog, open) {
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
}
