// Soundstage DSP engine — mid/side stereo width.
//
// Surround-safe by construction: it only touches the front L/R pair, and centred (mono) content is
// untouched (its side signal is zero), so vocals never wander. width = 1 is a perfect identity,
// width = 0 collapses to mono, width > 1 widens. This is the correct replacement for the old
// APO Copy-matrix that kept collapsing the image.
#pragma once

#include <algorithm>

namespace soundstage {

class StereoWidth {
public:
    void setWidth(double w) { width_ = std::max(0.0, std::min(2.0, w)); }  // 0..2, 1 = unchanged

    inline void process(double& l, double& r) const {
        const double mid = 0.5 * (l + r);
        const double side = 0.5 * (l - r) * width_;
        l = mid + side;
        r = mid - side;
    }

private:
    double width_ = 1.0;
};

}  // namespace soundstage
