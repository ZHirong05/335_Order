// Format locally before Blazor receives the input, including on mobile keyboards.
(() => {
    function atEnd(input) {
        input.setSelectionRange(input.value.length, input.value.length);
    }
    function moneyInput(input) {
        return input instanceof HTMLInputElement && input.classList.contains('money-input');
    }
    // A mouse/touch click can reposition the caret after focusin has fired.
    document.addEventListener('click', event => {
        if (moneyInput(event.target)) atEnd(event.target);
    });
    document.addEventListener('beforeinput', event => {
        if (!moneyInput(event.target)) return;
        // Keep digit entry and backspace anchored at the sen end of the amount.
        if (event.inputType === 'insertText' || event.inputType === 'deleteContentBackward') atEnd(event.target);
    }, true);
    document.addEventListener('input', event => {
        const input = event.target;
        if (!(input instanceof HTMLInputElement) || !input.classList.contains('money-input')) return;
        const digits = input.value.replace(/[^0-9]/g, '').replace(/^0+/, '');
        const sen = Math.min(Number(digits || '0'), 99999999900);
        input.value = (sen / 100).toFixed(2);
        atEnd(input);
    }, true);
    document.addEventListener('focusin', event => {
        const input = event.target;
        if (input instanceof HTMLInputElement && input.classList.contains('money-input')) atEnd(input);
    });
})();
