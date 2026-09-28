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
    let lastDescriptor = null;
    let referenceDescriptor = null;
    let faceVerified = false;
    let modelsPromise = null;
    const modelUrl = "/vendor/face-api/models";
    const detectorOptions = () => new faceapi.TinyFaceDetectorOptions({
        inputSize: 320,
        scoreThreshold: 0.75,
    });

    function requiresIdentityVerification() {
        if (!currentForm) return false;
        return new URL(currentForm.action, window.location.href)
            .pathname.toLowerCase() === "/check-in";
    }

    function loadModels() {
        if (!modelsPromise) {
            modelsPromise = Promise.all([
                faceapi.nets.tinyFaceDetector.loadFromUri(modelUrl),
                faceapi.nets.faceLandmark68TinyNet.loadFromUri(modelUrl),
                faceapi.nets.faceRecognitionNet.loadFromUri(modelUrl),
            ]);
        }
        return modelsPromise;
    }

    // Begin downloading and compiling the models as soon as the authenticated
    // page is idle. By the time most users click Check in, the expensive first
    // load is already complete and subsequent visits come from browser cache.
    const preloadModels = () => loadModels().catch(error => {
        console.warn("Face verification preload failed", error);
        modelsPromise = null;
    });
    if ("requestIdleCallback" in window) {
        window.requestIdleCallback(preloadModels, { timeout: 1000 });
    } else {
        window.setTimeout(preloadModels, 250);
    }

    async function loadReferenceDescriptor() {
        referenceDescriptor = null;
        const response = await fetch("/api/face/reference", {
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" },
        });
        if (!response.ok) throw new Error(`Reference request failed (${response.status})`);

        const data = await response.json();
        if (data.ready) return;
        if (!data.reference_photo) {
            throw new Error("No previous selfie is available. Open Face profile and enroll before checking in.");
        }

        const image = await faceapi.fetchImage(data.reference_photo);
        const result = await faceapi
            .detectSingleFace(image, detectorOptions())
            .withFaceLandmarks(true)
            .withFaceDescriptor();
        if (!result) {
            throw new Error("Your previous selfie cannot be used as a face reference. Please enroll a new face profile.");
        }
        referenceDescriptor = Array.from(result.descriptor);
    }

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
        lastDescriptor = null;
        faceVerified = false;
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
            const modelsReady = loadModels();
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
            status.textContent =
                "Camera ready. Loading face verification models (the first load may take about a minute)…";
            await modelsReady;
            if (requiresIdentityVerification()) {
                status.textContent = "Loading your enrolled face profile…";
                await loadReferenceDescriptor();
            }
            status.textContent = "Position only your face in the frame, then begin verification.";
        } catch (err) {
            console.warn("Face verification setup failed", err);
            status.textContent = err && err.message
                ? err.message
                : "Camera or face verification is unavailable. Please allow camera access and try again.";
            // Hide the Capture button so the user can't proceed without a photo.
            captureBtn.hidden = true;
        }
    }

    function delay(milliseconds) {
        return new Promise(resolve => setTimeout(resolve, milliseconds));
    }

    async function detectLiveFace() {
        const results = await faceapi
            .detectAllFaces(video, detectorOptions())
            .withFaceLandmarks(true)
            .withFaceDescriptors();
        if (results.length !== 1) return null;

        const box = results[0].detection.box;
        if (box.width < video.videoWidth * 0.22
            || box.height < video.videoHeight * 0.22) {
            return null;
        }
        return results[0];
    }

    async function verifyVisibleFace() {
        const deadline = Date.now() + 8000;
        while (Date.now() < deadline) {
            const result = await detectLiveFace();
            if (result) return result;
            status.textContent =
                "Show exactly one clear face, centered and close to the camera.";
            await delay(250);
        }

        throw new Error(
            "No clear face was detected. Remove hands or obstructions and keep one face visible.");
    }

    async function captureFrame() {
        if (!video.videoWidth) {
            status.textContent = "Camera not ready yet, try again.";
            return;
        }

        captureBtn.disabled = true;
        status.textContent = "Checking for one clear face…";

        let verifiedFace;
        try {
            await loadModels();
            verifiedFace = await verifyVisibleFace();
        } catch (err) {
            console.warn("Face verification failed", err);
            status.textContent = err && err.message
                ? err.message
                : "Face verification failed. Please try again.";
            captureBtn.disabled = false;
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

        lastDataUrl = canvas.toDataURL("image/jpeg", 0.78);
        lastDescriptor = Array.from(verifiedFace.descriptor);
        faceVerified = true;
        preview.src = lastDataUrl;

        captureBtn.disabled = false;
        video.hidden = true;
        preview.hidden = false;
        overlay.hidden = true;
        captureBtn.hidden = true;
        retakeBtn.hidden = false;
        confirmBtn.hidden = false;
        status.textContent = "Face verified. Click Confirm to submit.";
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
        if (!lastDescriptor || !faceVerified) {
            status.textContent = "Complete face verification before submitting.";
            return;
        }

        function setHidden(name, value) {
            let input = form.querySelector(`input[name="${name}"]`);
            if (!input) {
                input = document.createElement("input");
                input.type = "hidden";
                input.name = name;
                form.appendChild(input);
            }
            input.value = value;
        }
        if (requiresIdentityVerification()) {
            setHidden("face_descriptor", JSON.stringify(lastDescriptor));
            setHidden("reference_descriptor",
                referenceDescriptor ? JSON.stringify(referenceDescriptor) : "");
            setHidden("face_verification", "face-match-v1");
        }

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
            subtitle.textContent = "Show one clear face that matches your enrolled profile.";
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
