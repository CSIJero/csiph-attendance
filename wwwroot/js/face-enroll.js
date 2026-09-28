(function () {
    const form = document.getElementById("face-form");
    const historyForms = Array.from(
        document.querySelectorAll("[data-face-history-form]"));
    if (!form && historyForms.length === 0) return;

    const modelUrl = "/vendor/face-api/models";
    const options = () => new faceapi.TinyFaceDetectorOptions({
        inputSize: 320,
        scoreThreshold: 0.75,
    });
    const modelsReady = Promise.all([
        faceapi.nets.tinyFaceDetector.loadFromUri(modelUrl),
        faceapi.nets.faceLandmark68TinyNet.loadFromUri(modelUrl),
        faceapi.nets.faceRecognitionNet.loadFromUri(modelUrl),
    ]);

    async function descriptorFor(source) {
        await modelsReady;
        const results = await faceapi
            .detectAllFaces(source, options())
            .withFaceLandmarks(true)
            .withFaceDescriptors();
        if (results.length !== 1) {
            throw new Error("The reference photo must contain exactly one clear face.");
        }
        return Array.from(results[0].descriptor);
    }

    for (const historyForm of historyForms) {
        historyForm.addEventListener("submit", async event => {
            event.preventDefault();
            const button = historyForm.querySelector('button[type="submit"]');
            const image = historyForm.querySelector("img");
            const descriptorInput = historyForm.querySelector(
                'input[name="descriptor"]');
            if (!image || !descriptorInput) return;

            button.disabled = true;
            button.textContent = "Checking face…";
            try {
                descriptorInput.value = JSON.stringify(
                    await descriptorFor(image));
                if (window.confirm("Use this selfie as your reference?")) {
                    historyForm.submit();
                    return;
                }
            } catch (error) {
                window.alert(error.message || "Could not analyse that selfie.");
            }
            button.disabled = false;
            button.textContent = "Use this photo";
        });
    }

    if (!form) return;

    const video = document.getElementById("face-video");
    const canvas = document.getElementById("face-canvas");
    const preview = document.getElementById("face-preview");
    const photoInput = document.getElementById("face-photo");
    const descriptorInput = document.getElementById("face-descriptor");
    const captureButton = document.getElementById("face-capture");
    const submitButton = document.getElementById("face-submit");

    Promise.all([
        modelsReady,
        navigator.mediaDevices.getUserMedia({
            video: { facingMode: "user" },
            audio: false,
        }),
    ]).then(([, stream]) => {
        video.srcObject = stream;
    }).catch(error => {
        preview.textContent = `Camera or face detector unavailable: ${error.message}`;
        captureButton.disabled = true;
    });

    captureButton.addEventListener("click", async () => {
        if (!video.videoWidth) {
            preview.textContent = "Camera is not ready yet.";
            return;
        }

        captureButton.disabled = true;
        preview.textContent = "Checking for exactly one face…";
        try {
            const descriptor = await descriptorFor(video);
            canvas.width = video.videoWidth;
            canvas.height = video.videoHeight;
            canvas.getContext("2d").drawImage(
                video, 0, 0, canvas.width, canvas.height);
            const data = canvas.toDataURL("image/jpeg", 0.85);
            photoInput.value = data;
            descriptorInput.value = JSON.stringify(descriptor);
            preview.replaceChildren();
            const image = document.createElement("img");
            image.src = data;
            image.alt = "Captured preview";
            image.style.maxWidth = "200px";
            image.style.borderRadius = "8px";
            preview.appendChild(image);
            submitButton.disabled = false;
        } catch (error) {
            preview.textContent = error.message || "Could not analyse the face.";
        } finally {
            captureButton.disabled = false;
        }
    });
})();
