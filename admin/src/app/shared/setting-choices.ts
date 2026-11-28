/**
 * Settings that take one of a few words (T-486, T-493): picked from a list, each
 * word explained in the panel's language, so nothing has to be typed - the
 * backend refuses any other word anyway.
 */
export const SETTING_CHOICES: Readonly<Record<string, readonly string[]>> = {
  'Camera.Mode': ['Activity', 'Continuous'],
  'Camera.Resolution': ['640x480', '1280x720', '1920x1080'],
  'Camera.Quality': ['Low', 'Medium', 'High'],
  // T-493: the backend refuses any other word for these too.
  'Notification.MinSeverity': ['Critical', 'High', 'Medium'],
  'Privacy.SensitiveAutoDeleteDefault': [
    'Never',
    'AfterPrint',
    'AfterOneDay',
    'AfterSevenDays',
    'AfterThirtyDays',
  ],
  'Fiscal.TopUpReceiptMode': ['OnTopUp', 'OnService'],
};
