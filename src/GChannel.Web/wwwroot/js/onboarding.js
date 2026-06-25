// §9 phase 2: app-wide product tour built on Driver.js (loaded via CDN in App.razor).
// Exposed as an ES module imported by OnboardingTourService.

let activeDriver = null;

/**
 * Starts a guided tour.
 * @param {any} dotNetRef - .NET reference invoked with OnTourCompleted when the tour ends/closes.
 * @param {Array<{element:string,title:string,description:string,side?:string}>} steps
 */
export function startTour(dotNetRef, steps) {
    const factory = window.driver && window.driver.js && window.driver.js.driver;
    if (!factory) {
        console.warn('Onboarding tour: driver.js is not loaded; skipping.');
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnTourCompleted').catch(() => { });
        }
        return;
    }

    // Drop any step whose target isn't on the current page so the tour never stalls on a missing
    // element (steps without an element render as centred modals and are always kept).
    const usable = (steps || []).filter(s => !s.element || document.querySelector(s.element));
    if (usable.length === 0) {
        // Nothing to highlight yet (e.g. a page-scoped walkthrough whose elements haven't rendered).
        // Don't report completion so it can be retried later.
        return;
    }

    if (activeDriver) {
        try { activeDriver.destroy(); } catch { /* ignore */ }
        activeDriver = null;
    }

    activeDriver = factory({
        showProgress: true,
        allowClose: true,
        overlayOpacity: 0.6,
        nextBtnText: 'Next',
        prevBtnText: 'Back',
        doneBtnText: 'Done',
        steps: usable.map(s => {
            const popover = {
                title: s.title,
                description: s.description,
                side: s.side || 'bottom',
                align: 'start'
            };

            // Interactive (gated) step: block Next until the referenced input has a value.
            if (s.requireValueOf) {
                popover.onNextClick = () => {
                    const field = fieldValueElement(document.querySelector(s.requireValueOf));
                    const value = field ? (field.value || '').trim() : '';
                    if (!value) {
                        if (field) { field.focus(); }
                        return; // Stay on this step until the user fills it in.
                    }
                    if (activeDriver) { activeDriver.moveNext(); }
                };
            }

            return { element: s.element || undefined, popover };
        }),
        // Fires on completion AND on close/escape — either way onboarding state is marked done.
        onDestroyed: () => {
            activeDriver = null;
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnTourCompleted').catch(() => { });
            }
        }
    });

    activeDriver.drive();
}

// Resolves the actual value-bearing input within a target (MudBlazor wraps inputs in a div carrying
// the data-walkthrough hook), falling back to the element itself when it is already a form control.
function fieldValueElement(target) {
    if (!target) {
        return null;
    }
    if (target.matches && target.matches('input, textarea, select')) {
        return target;
    }
    return target.querySelector('input, textarea, select');
}
