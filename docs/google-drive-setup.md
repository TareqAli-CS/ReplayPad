# Setting up Google Drive backup (one time, for whoever builds ReplayPad)

ReplayPad signs in to Google **directly from the app** — there is no ReplayPad server. Google still needs to know *which app* is asking for access, so the person who builds and publishes ReplayPad registers it once and gets an **OAuth client ID**. It's free and takes about 5 minutes. Users of the published app never do this.

## 1. Create a Google Cloud project

1. Go to <https://console.cloud.google.com/> and sign in.
2. Top bar → project picker → **New project** → name it `ReplayPad` → **Create**, then select it.

## 2. Turn on the Drive API

**APIs & Services → Library** → search **Google Drive API** → **Enable**.

## 3. Consent screen (what users see when they sign in)

**APIs & Services → OAuth consent screen** (called *Google Auth Platform* in newer consoles):

1. **Get started** → App name `ReplayPad`, your support e-mail → Audience **External** → your contact e-mail → accept → **Create**.
2. **Data access → Add or remove scopes** → tick **`.../auth/drive.file`** ("See, edit, create and delete only the specific Google Drive files you use with this app") → **Update** → **Save**.
   - `drive.file` is a *non-sensitive* scope: no Google security review is required.
3. **Audience → Test users** → add your own Google address (and anyone testing with you).

## 4. Create the client ID

**APIs & Services → Credentials → Create credentials → OAuth client ID**

- Application type: **Desktop app**
- Name: `ReplayPad desktop`
- **Create** → copy the **Client ID** and **Client secret**.

(For desktop apps Google does not treat the "secret" as confidential — it ships inside every installed copy. Security comes from the user's consent in the browser plus PKCE, which ReplayPad uses.)

## 5. Give it to the build

In the `ReplayPad/` folder, copy `google-oauth.props.example` to **`google-oauth.props`** and paste the two values. That file is git-ignored, so it never ends up in the public repo. (Alternatively set the `GoogleClientId` / `GoogleClientSecret` environment variables before building.)

Rebuild — the ☁ window now shows **Sign in with Google**.

## 6. Going public

While the app is in **Testing**, only the test users from step 3 can sign in, and their sign-in expires after 7 days. When you're ready for everyone:

**OAuth consent screen → Audience → Publish app → Production.**

Because ReplayPad only uses `drive.file`, publishing doesn't need Google's security assessment. Until you optionally complete brand verification, users see a *"Google hasn't verified this app"* notice with **Advanced → Go to ReplayPad** — normal for small open-source tools.
