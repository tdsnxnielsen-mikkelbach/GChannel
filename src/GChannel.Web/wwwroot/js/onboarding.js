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
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnTourCompleted').catch(() => { });
        }
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
        steps: usable.map(s => ({
            element: s.element || undefined,
            popover: {
                title: s.title,
                description: s.description,
                side: s.side || 'bottom',
                align: 'start'
            }
        })),
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
