package com.focuswindow.client.transport;

import android.os.Bundle;
import com.focuswindow.client.transport.IAuthorityAudioRun;

interface IAuthorityAudioEnrollment {
    Bundle open(String generation, IAuthorityAudioRun run);
    void checkCurrent(String generation);
    void status(String generation, int state);
}
