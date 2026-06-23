// Selfie capture flow for check-in / check-out forms.
//
// Forms tagged with class "selfie-form" use a single shared modal in
// _SelfieModal.cshtml. When the user clicks the form's [data-selfie-trigger]
// button we open the camera, capture a JPEG into the form's hidden
// <input name="photo">, and then submit normally. Posting from the form
// (instead of fetch) keeps anti-forgery + flash messages working with no
// changes on the server.
(function () {
    const modal = document.getElementById("selfie-modal");
    if (!modal) return;

    const video = modal.querySelector("#selfie-video");
    const canvas = modal.querySelector("#selfie-canvas");
    const preview = modal.querySelector("#selfie-preview");
    const overlay = modal.querySelector("#selfie-overlay");
    const status = modal.querySelector("#selfie-status");
    const subtitle = modal.querySelector("#selfie-modal-subtitle");
    const captureBtn = modal.querySelector("#selfie-capture");
    const retakeBtn = modal.querySelector("#selfie-retake");
    const confirmBtn = modal.querySelector("#selfie-confirm");

    let currentForm = null;
    let stream = null;
    let lastDataUrl = null;

    function openModal() {
        modal.hidden = false;
        modal.setAttribute("aria-hidden", "false");
        document.body.style.overflow = "hidden";
    }

    function closeModal() {
        modal.hidden = true;
        modal.setAttribute("aria-hidden", "true");
        document.body.style.overflow = "";
        stopStream();
        resetView();
        currentForm = null;
    }

    function stopStream() {
        if (!stream) return;
        for (const track of stream.getTracks()) track.stop();
        stream = null;
        video.srcObject = null;
    }

    function resetView() {
        video.hidden = false;
        preview.hidden = true;
        overlay.hidden = false;
        captureBtn.hidden = false;
        retakeBtn.hidden = true;
        confirmBtn.hidden = true;
        lastDataUrl = null;
        preview.removeAttribute("src");
    }

    async function startCamera() {
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            status.textContent =
                "This browser doesn't support the camera. A selfie is required to "
                + "check in or out — please use a modern browser (Chrome / Edge / Firefox).";
            // No fallback submit: a selfie is mandatory.
            captureBtn.hidden = true;
            return;
        }

        status.textContent = "Starting camera…";
        try {
            stream = await navigator.mediaDevices.getUserMedia({
                video: {
                    facingMode: "user",
                    width: { ideal: 640 },
                    height: { ideal: 480 },
                },
                audio: false,
            });
            video.srcObject = stream;
            await video.play();
            status.textContent = "Position your face in the frame.";
        } catch (err) {
            console.warn("getUserMedia failed", err);
            status.textContent =
                "Camera unavailable or permission denied. A selfie is required "
                + "— please allow camera access in your browser settings and try again.";
            // Hide the Capture button so the user can't proceed without a photo.
            captureBtn.hidden = true;
        }
    }

    function captureFrame() {
        if (!video.videoWidth) {
            status.textContent = "Camera not ready yet, try again.";
            return;
        }

        // Downscale to ~480px on the long edge so the base64 payload stays
        // around 30-50KB. Anything larger bloats the SQLite DB pointlessly.
        const target = 480;
        const ratio = video.videoWidth / video.videoHeight;
        let cw, ch;
        if (ratio >= 1) { cw = target; ch = Math.round(target / ratio); }
        else            { ch = target; cw = Math.round(target * ratio); }

        canvas.width = cw;
        canvas.height = ch;
        const ctx = canvas.getContext("2d");
        // Mirror so the saved photo matches what the user sees on screen.
        ctx.translate(cw, 0);
        ctx.scale(-1, 1);
        ctx.drawImage(video, 0, 0, cw, ch);

        // Lock controls while we run the (potentially async) face check.
        captureBtn.disabled = true;
        status.textContent = "Checking that we can see your face…";

        detectFace(canvas).then((result) => {
            captureBtn.disabled = false;
            if (!result.ok) {
                status.textContent = result.message
                    || "We couldn't see a clear face. Please face the camera in good light and try again.";
                // Don't reveal the preview — the user must retry capture.
                return;
            }

            lastDataUrl = canvas.toDataURL("image/jpeg", 0.78);
            preview.src = lastDataUrl;

            video.hidden = true;
            preview.hidden = false;
            overlay.hidden = true;
            captureBtn.hidden = true;
            retakeBtn.hidden = false;
            confirmBtn.hidden = false;
            status.textContent = "Looks good. Click Confirm to submit, or Retake to try again.";
        }).catch((err) => {
            console.warn("Face check failed", err);
            captureBtn.disabled = false;
            // Don't block the user if our detector itself errors out — fall
            // back to accepting the frame so a broken heuristic can't make
            // clocking in impossible.
            lastDataUrl = canvas.toDataURL("image/jpeg", 0.78);
            preview.src = lastDataUrl;
            video.hidden = true;
            preview.hidden = false;
            overlay.hidden = true;
            captureBtn.hidden = true;
            retakeBtn.hidden = false;
            confirmBtn.hidden = false;
            status.textContent = "Captured. Click Confirm to submit, or Retake to try again.";
        });
    }

    // ----- Face detection -------------------------------------------------
    // We try the native Shape Detection API first (Chrome/Edge on Android,
    // some desktop builds behind a flag) because it's the cheapest and most
    // accurate option. When it's not available we fall back to a simple
    // skin-tone heuristic over the center region of the frame: enough to
    // catch "the camera is pointed at the ceiling" or "the lens is covered"
    // without pulling in a 1MB ML model.
    async function detectFace(srcCanvas) {
        try {
            if (typeof window !== "undefined" && "FaceDetector" in window) {
                const detector = new window.FaceDetector({ fastMode: true, maxDetectedFaces: 2 });
                const faces = await detector.detect(srcCanvas);
                if (faces && faces.length > 0) {
                    return { ok: true };
                }
                return {
                    ok: false,
                    message: "We couldn't see your face. Please center your face in the frame, make sure it's well lit, and try again.",
                };
            }
        } catch (err) {
            // Some browsers expose FaceDetector but throw when used (e.g.
            // missing platform support). Fall through to the heuristic.
            console.warn("FaceDetector unavailable, using fallback", err);
        }

        return heuristicFaceCheck(srcCanvas);
    }

    function heuristicFaceCheck(srcCanvas) {
        const ctx = srcCanvas.getContext("2d");
        const w = srcCanvas.width;
        const h = srcCanvas.height;
        // Sample the central 60% of the frame — where a face should be.
        const cx = Math.floor(w * 0.20);
        const cy = Math.floor(h * 0.15);
        const cw = Math.floor(w * 0.60);
        const ch = Math.floor(h * 0.70);

        let data;
        try {
            data = ctx.getImageData(cx, cy, cw, ch).data;
        } catch (err) {
            // CORS-tainted canvas etc. — accept the frame, the server still
            // validates the photo bytes.
            return { ok: true };
        }

        let total = 0;
        let skin = 0;
        let bright = 0;
        let lumaSum = 0;
        // Step over pixels in 4-pixel strides for speed.
        for (let i = 0; i < data.length; i += 16) {
            const r = data[i];
            const g = data[i + 1];
            const b = data[i + 2];
            total++;

            // Rec. 601 luma — covers most cases for "is this frame dark?".
            const luma = 0.299 * r + 0.587 * g + 0.114 * b;
            lumaSum += luma;
            if (luma > 40) bright++;

            // Loose skin-tone band that works across a range of skin colors
            // under typical webcam lighting. This is intentionally permissive
            // — we only need to reject "no person at all" frames.
            const maxC = Math.max(r, g, b);
            const minC = Math.min(r, g, b);
            const isSkin =
                r > 70 && g > 35 && b > 20
                && (maxC - minC) > 12
                && Math.abs(r - g) > 10
                && r > g && r > b;
            if (isSkin) skin++;
        }

        if (total === 0) return { ok: true };

        const avgLuma = lumaSum / total;
        const skinRatio = skin / total;
        const brightRatio = bright / total;

        if (avgLuma < 25 || brightRatio < 0.20) {
            return {
                ok: false,
                message: "The frame looks very dark. Please move to better lighting and try again.",
            };
        }
        if (skinRatio < 0.04) {
            return {
                ok: false,
                message: "We couldn't see a face in the frame. Please face the camera and try again.",
            };
        }
        return { ok: true };
    }

    function submitWithPhoto() {
        return submitWithPhotoAsync();
    }

    async function submitWithPhotoAsync() {
        if (!currentForm) return;
        // Hard guard: refuse to submit without a captured photo. The Confirm
        // button is only revealed after a successful capture, but this is
        // the belt-and-suspenders check in case the button is reached some
        // other way.
        if (!lastDataUrl) {
            status.textContent = "Please capture a selfie before submitting.";
            return;
        }
        const form = currentForm;
        const hidden = form.querySelector('input[name="photo"]');
        if (hidden) hidden.value = lastDataUrl;

        // Geolocation: attach lat/lng/accuracy when the user permits it.
        // Onsite Coalition check-ins are validated against configured
        // sites server-side; the geofence helper returns "MissingCoords"
        // when the form doesn't ship coordinates, so we try our best to
        // include them before submitting.
        try {
            await attachGeolocation(form);
        } catch (e) {
            // Geo is best-effort — fall through to the submit.
            console.warn("Geolocation skipped", e);
        }

        closeModal();
        form.submit();
    }

    // Wire up form triggers. Buttons inside .selfie-form with a
    // [data-selfie-trigger] attribute open the modal instead of
    // submitting directly.
    document.addEventListener("click", (e) => {
        const trigger = e.target.closest("[data-selfie-trigger]");
        if (!trigger) return;
        const form = trigger.closest(".selfie-form");
        if (!form) return;

        e.preventDefault();
        currentForm = form;

        // Customize subtitle per action so the modal makes sense for both
        // check-in and check-out without duplicating the partial.
        const action = (trigger.dataset.selfieAction || "").toLowerCase();
        if (action === "check-out") {
            modal.querySelector("#selfie-modal-title").textContent = "Selfie before check-out";
            subtitle.textContent = "One last selfie before we close out your shift.";
        } else {
            modal.querySelector("#selfie-modal-title").textContent = "Selfie for check-in";
            subtitle.textContent = "Look at the camera, then click Capture.";
        }

        openModal();
        resetView();
        startCamera();
    });

    captureBtn.addEventListener("click", captureFrame);
    retakeBtn.addEventListener("click", () => {
        resetView();
        // Camera stream is still alive, just unhide the video.
    });
    confirmBtn.addEventListener("click", submitWithPhoto);

    for (const el of modal.querySelectorAll("[data-selfie-cancel]")) {
        el.addEventListener("click", closeModal);
    }
    modal.addEventListener("click", (e) => {
        if (e.target === modal) closeModal();
    });
    document.addEventListener("keydown", (e) => {
        if (!modal.hidden && e.key === "Escape") closeModal();
    });

    /**
     * Reads navigator.geolocation and writes lat/lng/accuracy into the
     * form's hidden inputs (created on the fly when absent). Resolves
     * within ~6s — long enough for a decent GPS fix indoors but short
     * enough that a check-in still feels snappy when geo is denied.
     */
    function attachGeolocation(form) {
        return new Promise((resolve) => {
            if (!navigator.geolocation || !form) return resolve();
            const TIMEOUT_MS = 6000;
            let settled = false;

            const finalize = () => { if (!settled) { settled = true; resolve(); } };
            setTimeout(finalize, TIMEOUT_MS + 500);

            navigator.geolocation.getCurrentPosition(
                (pos) => {
                    if (settled) return;
                    setOrCreateHidden(form, "lat", pos.coords.latitude.toFixed(6));
                    setOrCreateHidden(form, "lng", pos.coords.longitude.toFixed(6));
                    setOrCreateHidden(form, "accuracy", String(Math.round(pos.coords.accuracy || 0)));
                    finalize();
                },
                () => finalize(),
                { enableHighAccuracy: true, timeout: TIMEOUT_MS, maximumAge: 30000 }
            );
        });
    }

    function setOrCreateHidden(form, name, value) {
        let input = form.querySelector(`input[name="${name}"]`);
        if (!input) {
            input = document.createElement("input");
            input.type = "hidden";
            input.name = name;
            form.appendChild(input);
        }
        input.value = value;
    }
})();
