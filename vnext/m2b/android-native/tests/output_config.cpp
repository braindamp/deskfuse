#include "output_config.h"
#include "timing_sample.h"
#include <stdexcept>
#include <initializer_list>
static void expect(bool value) {if (!value) throw std::runtime_error("invalid output policy validation");}
int main() {
    df_audio::OutputConfigV8 experiment;
    expect(df_audio::valid_output_config(experiment));
    for (uint32_t reserve : {1440u,1200u,960u,720u,480u}) {
        experiment.reserve_frames=reserve;
        expect(df_audio::valid_output_config(experiment));
    }
    experiment.reserve_frames=479;expect(!df_audio::valid_output_config(experiment));
    experiment.reserve_frames=1440;experiment.sharing_policy=2;
    expect(!df_audio::valid_output_config(experiment));
    experiment.sharing_policy=1;experiment.usage_policy=2;
    expect(!df_audio::valid_output_config(experiment));
    experiment.usage_policy=1;experiment.reserved1=1;
    expect(!df_audio::valid_output_config(experiment));
    df_audio::OutputConfigV7 c{};
    expect(df_audio::valid_output_config(c));
    for (uint32_t policy : {0u,2u,3u}) {c.buffer_bursts=policy;expect(df_audio::valid_output_config(c));}
    c.buffer_bursts=1;expect(!df_audio::valid_output_config(c));
    c.buffer_bursts=3;c.size=12;expect(!df_audio::valid_output_config(c));
    c.size=16;c.version=6;expect(!df_audio::valid_output_config(c));
    c.version=7;c.reserved=1;expect(!df_audio::valid_output_config(c));
    expect(df_audio::requested_buffer_frames(0,0)==0);
    expect(df_audio::requested_buffer_frames(3,960)==2880);
    expect(df_audio::requested_buffer_frames(2,960)==1920);
    expect(df_audio::requested_buffer_frames(3,0)==-1);
    expect(df_audio::requested_buffer_frames(1,960)==-1);
    expect(df_audio::requested_buffer_frames(3,std::numeric_limits<int32_t>::max())==-1);
}
