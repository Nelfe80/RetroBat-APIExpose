# APIExpose License Summary

This summary is provided for convenience. The full terms are in `PERSONAL-LICENSE.md` and `COMMERCIAL-LICENSE.md`.

---

## 🚀 Activation System (Trust-Based)

To respect our users, no restrictive hardware lock or online activation DRM is implemented. Licensing operates on **honesty and a simple local file declaration**.

To activate a license, create a file named `license.ini` at the root of the installation folder:

```ini
[License]
# Your unique license identifier
LicenseId=APX-PRS-2026-000381
# The name of the licensee / owner
Licensee=Jean Dupont
```

---

## 👤 Personal Use

*   **Rule**: 1 personal machine = 1 personal license (`APX-PRS-YYYY-NNNNNN`).
*   Each separate machine must have its own unique license ID configured in `license.ini`.

---

## 💼 Commercial Use

*   **Rule**: 1 commercial machine sold = 1 reseller license (`APX-RSU-YYYY-NNNNNN`).
*   Commercial use includes pre-installing, bundling, or selling APIExpose as part of any cabinet, mini-PC, hard drive, SD card, digital package, or support offer.
*   Resellers must supply a unique `license.ini` with each machine sold.

---

## 📄 License Identifiers

```text
APX-PRS-YYYY-NNNNNN   Personal machine license
APX-RSU-YYYY-NNNNNN   Reseller unit license
APX-RSB-YYYY-NNNNNN   Reseller batch license
APX-CERT-YYYY-NNNNNN  Unit certificate attached to a batch
APX-DEV-YYYY-NNNNNN   Developer/test license
```
