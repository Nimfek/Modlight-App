# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Report it privately through
GitHub: open this repository's **Security** tab and choose **Report a vulnerability**.

Include what you found, how to reproduce it, and what an attacker could do with it. You
will get an answer, and a fix will be released before the details are made public.

A problem on the server side (the panel, the API, file pages) belongs to
[Modlight](https://github.com/Nimfek/Modlight/security); if you are not sure which side it
is on, report it in either place.

## In scope

- anything that gets code run on a user's machine: the update channel, downloaded
  components (FFmpeg, yt-dlp, models), file associations, the installer
- the browser sign-in: a token that reaches anything other than the app on `127.0.0.1`,
  or that the app accepts from somewhere else
- the app sending the account's token, files or settings to a server other than the one
  the user signed in to
- an upload, download or link going somewhere other than where the user asked

## Hardening already in place

- The update source is fixed at build time (`ModlightDefaultHost`) and never taken from the
  sign-in box; a build without one does not update itself at all.
- An installer is run only after its SHA-256 matches the one in the release manifest, and
  a manifest without a checksum is refused.
- Optional components are downloaded over HTTPS and checked against known SHA-256 values.
- The API token is sent in a header, never in a URL, so it does not end up in server logs.

Vulnerabilities in code inherited unchanged from ShareX are best reported to
[ShareX](https://github.com/ShareX/ShareX/security) as well, so every user benefits.
